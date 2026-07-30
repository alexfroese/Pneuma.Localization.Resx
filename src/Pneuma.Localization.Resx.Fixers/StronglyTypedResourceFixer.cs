using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
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

        foreach (var diagnostic in context.Diagnostics)
        {
            if (
                !diagnostic.Properties.TryGetValue("memberName", out var memberName)
                || memberName is null
            )
                return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Use strongly typed accessor",
                    ct => Fix(context.Document, node, memberName, ct),
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
        CancellationToken cancellationToken
    )
    {
        var editor = await DocumentEditor
            .CreateAsync(document, cancellationToken)
            .ConfigureAwait(false);

        var newNode = editor.Generator.MemberAccessExpression(node.Expression, memberName);

        editor.ReplaceNode(node, newNode);

        return editor.GetChangedDocument();
    }
}
