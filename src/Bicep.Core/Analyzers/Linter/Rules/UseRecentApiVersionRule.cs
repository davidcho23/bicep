// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Bicep.Core.ApiVersion;
using Bicep.Core.CodeAction;
using Bicep.Core.Diagnostics;
using Bicep.Core.Parsing;
using Bicep.Core.Resources;
using Bicep.Core.Semantics;
using Bicep.Core.Syntax;

namespace Bicep.Core.Analyzers.Linter.Rules
{
    // Adds linter rule to flag an issue when api version used in resource is not the latest
    // 1. Any GA version is allowed as long as it's not > 2 years old, even if there is a more recent GA version
    // 2. If there is no GA apiVersion less than 2 years old, then take the latest one available from the cache of GA versions
    // 3. A preview version(api version with -preview prefix) is valid only if it is latest and there is no later GA version
    // 4. For non preview versions(e.g. alpha, beta, privatepreview and rc), order of preference is latest GA -> Preview -> Non Preview 
    public sealed class UseRecentApiVersionRule : LinterRuleBase
    {
        public new const string Code = "use-recent-api-version";

        public UseRecentApiVersionRule() : base(
            code: Code,
            description: CoreResources.UseRecentApiVersionRuleDescription,
            docUri: new Uri($"https://aka.ms/bicep/linter/{Code}"))
        {
        }

        override public IEnumerable<IDiagnostic> AnalyzeInternal(SemanticModel model)
        {
            var spanFixes = new Dictionary<TextSpan, CodeFix>();
            var visitor = new Visitor(spanFixes, model);
            visitor.Visit(model.SourceFile.ProgramSyntax);

            return spanFixes.Select(kvp => CreateFixableDiagnosticForSpan(kvp.Key, kvp.Value));
        }

        public sealed class Visitor : SyntaxVisitor
        {
            private readonly IApiVersionProvider apiVersionProvider;
            private readonly SemanticModel model;
            private readonly Dictionary<TextSpan, CodeFix> spanFixes;

            public Visitor(Dictionary<TextSpan, CodeFix> spanFixes, SemanticModel model)
            {
                this.apiVersionProvider = model.ApiVersionProvider;
                this.spanFixes = spanFixes;
                this.model = model;
            }

            public override void VisitResourceDeclarationSyntax(ResourceDeclarationSyntax resourceDeclarationSyntax)
            {
                ResourceSymbol resourceSymbol = new ResourceSymbol(model.SymbolContext, resourceDeclarationSyntax.Name.IdentifierName, resourceDeclarationSyntax);

                if (resourceSymbol.TryGetResourceTypeReference() is ResourceTypeReference resourceTypeReference &&
                    resourceTypeReference.ApiVersion is string apiVersion &&
                    GetReplacementSpan(resourceSymbol, apiVersion) is TextSpan replacementSpan)
                {
                    string fullyQualifiedType = resourceTypeReference.FormatType();
                    (string? currentApiVersion, string? suffix) = apiVersionProvider.ParseApiVersion(apiVersion);

                    string? recentGAVersion = apiVersionProvider.GetRecentApiVersion(fullyQualifiedType, ApiVersionSuffixConstants.GA);

                    if (string.IsNullOrEmpty(suffix))
                    {
                        AddCodeFixIfGAVersionIsNotLatest(replacementSpan,
                                                         recentGAVersion,
                                                         currentApiVersion);
                    }
                    else
                    {
                        string? recentNonPreviewVersion = apiVersionProvider.GetRecentApiVersion(fullyQualifiedType, suffix);
                        string? recentPreviewVersion = apiVersionProvider.GetRecentApiVersion(fullyQualifiedType, ApiVersionSuffixConstants.Preview);

                        AddCodeFixIfNonGAVersionIsNotLatest(replacementSpan,
                                                            recentGAVersion,
                                                            recentPreviewVersion,
                                                            recentNonPreviewVersion,
                                                            suffix,
                                                            currentApiVersion);
                    }
                }

                base.VisitResourceDeclarationSyntax(resourceDeclarationSyntax);
            }

            private TextSpan? GetReplacementSpan(ResourceSymbol resourceSymbol, string apiVersion)
            {
                if (resourceSymbol.DeclaringResource.TypeString is StringSyntax typeString &&
                    typeString.StringTokens.First() is Token token)
                {
                    int replacementSpanStart = token.Span.Position + token.Text.IndexOf(apiVersion);

                    return new TextSpan(replacementSpanStart, apiVersion.Length);
                }

                return null;
            }

            // 1. Any GA version is allowed as long as it's not > 2 years old, even if there is a more recent GA version
            // 2. If there is no GA apiVersion less than 2 years old, then take the latest one available from the cache of GA versions
            public void AddCodeFixIfGAVersionIsNotLatest(TextSpan span,
                                                         string? recentGAVersion,
                                                         string? currentApiVersion)
            {
                if (currentApiVersion is null || recentGAVersion is null)
                {
                    return;
                }

                DateTime currentApiVersionDate = DateTime.Parse(currentApiVersion);

                if (DateTime.Now.Year - currentApiVersionDate.Year <= 2)
                {
                    return;
                }

                DateTime recentGAVersionDate = DateTime.Parse(recentGAVersion);

                if (DateTime.Compare(recentGAVersionDate, currentApiVersionDate) > 0)
                {
                    AddCodeFix(span, recentGAVersion);
                }
            }

            // A preview version is valid only if it is latest and there is no later GA version
            // For non preview versions like alpha, beta, privatepreview and rc, order of preference is latest GA -> Preview -> Non Preview 
            public void AddCodeFixIfNonGAVersionIsNotLatest(TextSpan span,
                                                            string? recentGAVersion,
                                                            string? recentPreviewVersion,
                                                            string? recentNonPreviewVersion,
                                                            string prefix,
                                                            string? currentVersion)
            {
                if (currentVersion is null)
                {
                    return;
                }

                DateTime currentVersionDate = DateTime.Parse(currentVersion);

                Dictionary<string, DateTime> prefixToRecentApiVersionMap = new Dictionary<string, DateTime>();

                if (prefix.Equals(ApiVersionSuffixConstants.Preview))
                {
                    if (recentGAVersion is not null)
                    {
                        prefixToRecentApiVersionMap.Add(recentGAVersion, DateTime.Parse(recentGAVersion));
                    }

                    if (recentPreviewVersion is not null)
                    {
                        prefixToRecentApiVersionMap.Add(recentPreviewVersion + prefix, DateTime.Parse(recentPreviewVersion));
                    }
                }
                else
                {
                    if (recentGAVersion is not null)
                    {
                        prefixToRecentApiVersionMap.Add(recentGAVersion, DateTime.Parse(recentGAVersion));
                    }

                    if (recentNonPreviewVersion is not null)
                    {
                        prefixToRecentApiVersionMap.Add(recentNonPreviewVersion + prefix, DateTime.Parse(recentNonPreviewVersion));
                    }

                    if (recentPreviewVersion is not null)
                    {
                        prefixToRecentApiVersionMap.Add(recentPreviewVersion + ApiVersionSuffixConstants.Preview, DateTime.Parse(recentPreviewVersion));
                    }
                }

                if (prefixToRecentApiVersionMap.Any())
                {
                    var sortedPrefixToRecentApiVersionDateMap = prefixToRecentApiVersionMap.OrderByDescending(x => x.Value).ToDictionary(x => x.Key, x => x.Value);

                    KeyValuePair<string, DateTime> kvp = sortedPrefixToRecentApiVersionDateMap.First();

                    if (DateTime.Compare(kvp.Value, currentVersionDate) > 0)
                    {
                        AddCodeFix(span, kvp.Key);
                    }
                }
            }

            private void AddCodeFix(TextSpan span, string apiVersion)
            {
                var codeReplacement = new CodeReplacement(span, apiVersion);
                string description = string.Format(CoreResources.UseRecentApiVersionRuleFixMessageFormat, apiVersion);
                var fix = new CodeFix(description, true, codeReplacement);
                spanFixes[span] = fix;
            }
        }
    }
}