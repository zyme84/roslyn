// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.RemoveUnnecessaryImports
{
    internal partial class AbstractCSharpRemoveUnnecessaryImportsService
    {
        private class Rewriter : CSharpSyntaxRewriter
        {
            private readonly ISet<UsingDirectiveSyntax> _unnecessaryUsingsDoNotAccessDirectly;
            private readonly CancellationToken _cancellationToken;

            public Rewriter(ISet<UsingDirectiveSyntax> unnecessaryUsings, CancellationToken cancellationToken)
                : base(visitIntoStructuredTrivia: true)
            {
                _unnecessaryUsingsDoNotAccessDirectly = unnecessaryUsings;
                _cancellationToken = cancellationToken;
            }

            public override SyntaxNode DefaultVisit(SyntaxNode node)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return base.DefaultVisit(node);
            }

            private void ProcessUsings(
                SyntaxList<UsingDirectiveSyntax> usings,
                ISet<UsingDirectiveSyntax> usingsToRemove,
                out SyntaxList<UsingDirectiveSyntax> finalUsings,
                out SyntaxTriviaList finalTrivia)
            {
                var currentUsings = new List<UsingDirectiveSyntax>(usings);

                finalTrivia = default(SyntaxTriviaList);
                for (int i = 0; i < usings.Count; i++)
                {
                    if (usingsToRemove.Contains(usings[i]))
                    {
                        var currentUsing = currentUsings[i];
                        currentUsings[i] = null;

                        var leadingTrivia = currentUsing.GetLeadingTrivia();
                        if (leadingTrivia.Any(t => t.Kind() != SyntaxKind.EndOfLineTrivia && t.Kind() != SyntaxKind.WhitespaceTrivia))
                        {
                            // This using had trivia we want to preserve.  If we're the last
                            // directive, then copy this trivia out so that our caller can place
                            // it on the next token.  If there is any directive following us,
                            // then place it on that.
                            if (i < usings.Count - 1)
                            {
                                currentUsings[i + 1] = currentUsings[i + 1].WithPrependedLeadingTrivia(leadingTrivia);
                            }
                            else
                            {
                                finalTrivia = leadingTrivia;
                            }
                        }
                    }
                }

                finalUsings = currentUsings.WhereNotNull().ToSyntaxList();
            }

            private ISet<UsingDirectiveSyntax> GetUsingsToRemove(
                SyntaxList<UsingDirectiveSyntax> oldUsings,
                SyntaxList<UsingDirectiveSyntax> newUsings)
            {
                Contract.Requires(oldUsings.Count == newUsings.Count);

                var result = new HashSet<UsingDirectiveSyntax>();
                for (int i = 0; i < oldUsings.Count; i++)
                {
                    if (_unnecessaryUsingsDoNotAccessDirectly.Contains(oldUsings[i]))
                    {
                        result.Add(newUsings[i]);
                    }
                }

                return result;
            }

            public override SyntaxNode VisitCompilationUnit(CompilationUnitSyntax node)
            {
                var compilationUnit = (CompilationUnitSyntax)base.VisitCompilationUnit(node);

                var usingsToRemove = GetUsingsToRemove(node.Usings, compilationUnit.Usings);
                if (usingsToRemove.Count == 0)
                {
                    return compilationUnit;
                }

                SyntaxList<UsingDirectiveSyntax> finalUsings;
                SyntaxTriviaList finalTrivia;
                ProcessUsings(compilationUnit.Usings, usingsToRemove, out finalUsings, out finalTrivia);

                // If there was any left over trivia, then attach it to the next token that
                // follows the usings.
                if (finalTrivia.Count > 0)
                {
                    var nextToken = compilationUnit.Usings.Last().GetLastToken().GetNextToken();
                    compilationUnit = compilationUnit.ReplaceToken(nextToken, nextToken.WithPrependedLeadingTrivia(finalTrivia));
                }

                var resultCompilationUnit = compilationUnit.WithUsings(finalUsings);
                if (finalUsings.Count == 0 &&
                    resultCompilationUnit.Externs.Count == 0 &&
                    resultCompilationUnit.Members.Count >= 1)
                {
                    // We've removed all the usings and now the first thing in the namespace is a
                    // type.  In this case, remove any newlines preceding the type.
                    var firstToken = resultCompilationUnit.GetFirstToken();
                    var newFirstToken = StripNewLines(firstToken);
                    resultCompilationUnit = resultCompilationUnit.ReplaceToken(firstToken, newFirstToken);
                }

                return resultCompilationUnit;
            }

            public override SyntaxNode VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
            {
                var namespaceDeclaration = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node);
                var usingsToRemove = GetUsingsToRemove(node.Usings, namespaceDeclaration.Usings);
                if (usingsToRemove.Count == 0)
                {
                    return namespaceDeclaration;
                }

                SyntaxList<UsingDirectiveSyntax> finalUsings;
                SyntaxTriviaList finalTrivia;
                ProcessUsings(namespaceDeclaration.Usings, usingsToRemove, out finalUsings, out finalTrivia);

                // If there was any left over trivia, then attach it to the next token that
                // follows the usings.
                if (finalTrivia.Count > 0)
                {
                    var nextToken = namespaceDeclaration.Usings.Last().GetLastToken().GetNextToken();
                    namespaceDeclaration = namespaceDeclaration.ReplaceToken(nextToken, nextToken.WithPrependedLeadingTrivia(finalTrivia));
                }

                var resultNamespace = namespaceDeclaration.WithUsings(finalUsings);
                if (finalUsings.Count == 0 &&
                    resultNamespace.Externs.Count == 0 &&
                    resultNamespace.Members.Count >= 1)
                {
                    // We've removed all the usings and now the first thing in the namespace is a
                    // type.  In this case, remove any newlines preceding the type.
                    var firstToken = resultNamespace.Members.First().GetFirstToken();
                    var newFirstToken = StripNewLines(firstToken);
                    resultNamespace = resultNamespace.ReplaceToken(firstToken, newFirstToken);
                }

                return resultNamespace;
            }

            private static SyntaxToken StripNewLines(SyntaxToken firstToken)
            {
                return firstToken.WithLeadingTrivia(firstToken.LeadingTrivia.SkipWhile(t => t.Kind() == SyntaxKind.EndOfLineTrivia));
            }
        }
    }
}
