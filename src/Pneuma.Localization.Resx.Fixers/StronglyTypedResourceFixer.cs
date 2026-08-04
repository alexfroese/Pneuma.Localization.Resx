using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Simplification;
using Pneuma.Localization.Resx.Generators;

namespace Pneuma.Localization.Resx.Fixers;

[ExportCodeFixProvider(LanguageNames.CSharp)]
public class StronglyTypedResourceFixer : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        [StronglyTypedResxAnalyzer.UseStronglyTypedResourceId];

    public override FixAllProvider? GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        if (
            await context
                .Document.GetSyntaxRootAsync(context.CancellationToken)
                .ConfigureAwait(false)
            is not SyntaxNode root
        )
            return;

        if (
            root.FindNode(context.Span, getInnermostNodeForTie: true)
            is not ElementAccessExpressionSyntax node
        )
            return;

        var model = await context
            .Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);

        foreach (var diagnostic in context.Diagnostics)
        {
            if (
                !diagnostic.Properties.TryGetValue("memberName", out var memberName)
                || memberName is null
            )
                continue;

            if (
                !diagnostic.Properties.TryGetValue("extensionContainer", out var extensionContainer)
                || extensionContainer is null
            )
                continue;

            if (
                model?.Compilation.GetTypeByMetadataName(extensionContainer)
                is not INamedTypeSymbol extensionType
            )
                continue;

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Use strongly typed accessor",
                    ct => Fix(context.Document, node, memberName, extensionType, ct),
                    equivalenceKey: nameof(StronglyTypedResourceFixer)
                ),
                diagnostic
            );
        }
    }

    private static async Task<Document> Fix(
        Document document,
        ElementAccessExpressionSyntax node,
        string memberName,
        INamedTypeSymbol extensionType,
        CancellationToken cancellationToken
    )
    {
        var editor = await DocumentEditor
            .CreateAsync(document, cancellationToken)
            .ConfigureAwait(false);

        var newNode = editor
            .Generator.TypeExpression(extensionType)
            .CopyAnnotationsTo(
                editor
                    .Generator.MemberAccessExpression(node.Expression, memberName)
                    .WithAdditionalAnnotations(Simplifier.AddImportsAnnotation)
            );

        editor.ReplaceNode(node, newNode);

        return editor.GetChangedDocument();
    }
}
