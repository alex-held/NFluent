﻿using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NFluent.Analyzer
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NFluentAnalyzerCodeFixProvider)), Shared]
    public class NFluentAnalyzerCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(NFluentAnalyzer.MissingCheckId, NFluentAnalyzer.SutIsTheCheckId);

        public sealed override FixAllProvider GetFixAllProvider()
        {
            // See https://github.com/dotnet/roslyn/blob/master/docs/analyzers/FixAllProvider.md for more information on Fix All Providers
            return WellKnownFixAllProviders.BatchFixer;
        }

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            foreach (var contextDiagnostic in context.Diagnostics)
            {

                var invocationExpression = root.FindToken(contextDiagnostic.Location.SourceSpan.Start).Parent.AncestorsAndSelf()
                    .OfType<InvocationExpressionSyntax>().First();
                if (contextDiagnostic.Id == NFluentAnalyzer.MissingCheckId)
                {
                    FixMissingCheck(context, invocationExpression, contextDiagnostic);
                }
                else if (contextDiagnostic.Id == NFluentAnalyzer.SutIsTheCheckId)
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(CodeFixResources.ExpandBinaryExpressionTitle,
                            c => ConvertExpressionSut(context.Document, invocationExpression, c)), 
                        contextDiagnostic);
                }
            }
        }

        private static async Task<Document> ConvertExpressionSut(Document contextDocument, InvocationExpressionSyntax invocationExpression, CancellationToken cancellationToken)
        {
            var thatNode =
                NFluentAnalyzer.FindInvocationOfThat(await contextDocument.GetSemanticModelAsync(cancellationToken),
                    invocationExpression);
            if (thatNode == null)
            {
                return contextDocument;
            }

            var sut = thatNode.ArgumentList.Arguments[0].Expression;
            var actualCheck = thatNode.Parent as MemberAccessExpressionSyntax;
            if (sut is BinaryExpressionSyntax binaryExpressionSyntax &&
                (actualCheck.HasName("IsTrue") || actualCheck.HasName("IsFalse")))
            {
                var realSut = binaryExpressionSyntax.Left;
                var refValue = binaryExpressionSyntax.Right;
                if (realSut is LiteralExpressionSyntax)
                {
                    refValue = realSut;
                    realSut = binaryExpressionSyntax.Right;
                }

                var checkName = string.Empty;
                switch (binaryExpressionSyntax.OperatorToken.Kind())
                {
                    case SyntaxKind.EqualsEqualsToken:
                        checkName = "IsEqualTo";
                        break;
                }

                if (!string.IsNullOrEmpty(checkName))
                {
                    var fix = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.InvocationExpression(
                                thatNode.Expression,
                                RoslynHelper.BuildArgumentList(realSut)
                                ), SyntaxFactory.IdentifierName(checkName)), 
                        RoslynHelper.BuildArgumentList(refValue));

                    var root = await contextDocument.GetSyntaxRootAsync(cancellationToken);
                    return contextDocument.WithSyntaxRoot(root.ReplaceNode(invocationExpression.Parent.Parent, fix));
                }
            }

            return contextDocument;
        }

        private static void FixMissingCheck(CodeFixContext context, InvocationExpressionSyntax invocationExpression,
            Diagnostic contextDiagnostic)
        {
            if (invocationExpression.Expression.Kind() == SyntaxKind.SimpleMemberAccessExpression &&
                invocationExpression.ArgumentList.Arguments.Any())
            {
                var memberAccess = (MemberAccessExpressionSyntax) invocationExpression.Expression;
                if (memberAccess.Expression is IdentifierNameSyntax)
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(CodeFixResources.AddSimpleCheckTitle,
                            c => AddAutomaticCheckMethod(context.Document, invocationExpression, c),
                            nameof(CodeFixResources.AddSimpleCheckTitle)),
                        contextDiagnostic);
                }
            }
        }

        private static async Task<Document> AddAutomaticCheckMethod(Document document, ExpressionSyntax invocationExpression,
            CancellationToken cancellationToken)
        {
            // Get the symbol representing the type to be renamed.
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

            var info = semanticModel.GetSymbolInfo(invocationExpression);
            var sutType = ((IMethodSymbol) info.Symbol).Parameters[0].Type;
            var replacementNode = BuildCorrectCheckThatExpression(invocationExpression, sutType);

            if (replacementNode == null)
            {
                return document;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            return document.WithSyntaxRoot(root.ReplaceNode(invocationExpression, replacementNode));
        }

        private static InvocationExpressionSyntax BuildCorrectCheckThatExpression(
            ExpressionSyntax invocationExpression, ITypeSymbol sutType)
        {
            var checkName = string.Empty;

            // deal with well known types
            switch (sutType.SpecialType)
            {
                case SpecialType.System_Boolean:
                    checkName = "IsTrue";
                    // When we have a reference type
                    break;
                case SpecialType.System_String:
                    checkName = "IsNotEmpty";
                    break;
                case SpecialType.System_Enum:
                    break;
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Decimal:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                    checkName = "IsNotZero";
                    break;
                case SpecialType.System_DateTime:
                    break;
                case SpecialType.System_IAsyncResult:
                    break;
                case SpecialType.System_AsyncCallback:
                    break;
                default:
                    if (sutType.TypeKind == TypeKind.Array || sutType.AllInterfaces.Any( t => t.SpecialType == SpecialType.System_Collections_IEnumerable))
                    {
                        return SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, invocationExpression, SyntaxFactory.IdentifierName("Not")),
                                SyntaxFactory.IdentifierName("IsEmpty")));
                    }
                    if (sutType.IsReferenceType || sutType.OriginalDefinition?.SpecialType == SpecialType.System_Nullable_T)
                    {
                        checkName = "IsNotNull";
                    }

                    break;
            }

            InvocationExpressionSyntax replacementNode;
            if (!string.IsNullOrEmpty(checkName))
            {
                // no fix applied

                replacementNode = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        invocationExpression,
                        SyntaxFactory.IdentifierName(checkName)));
            }
            else
            {
                replacementNode = null;
            }

            return replacementNode;
        }
    }
}