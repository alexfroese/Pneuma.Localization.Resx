using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pneuma.Localization.Resx.Generators;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class StronglyTypedResxAnalyzer : DiagnosticAnalyzer
{
    public const string UseStronglyTypedResourceId = "RESX001";
    public const string AddKeyToResourceFile = "RESX002";

    private static readonly DiagnosticDescriptor s_useStronglyTyped = new(
        UseStronglyTypedResourceId,
        "UseStronglyTypedResource",
        "Use strongly typed resource `{0}.{1}`",
        "Resource",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true
    );

    private static readonly DiagnosticDescriptor s_addKeyToResourceFile = new(
        AddKeyToResourceFile,
        "AddKeyToResx",
        "Add key '{0}' to {1}.resx",
        "Resource",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [s_useStronglyTyped, s_addKeyToResourceFile];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterCompilationStartAction(ctx =>
        {
            if (
                ctx.Compilation.GetTypeByMetadataName(
                    "Microsoft.Extensions.Localization.IStringLocalizer`1"
                )
                is not INamedTypeSymbol stringLocalizer
            )
                return;

            ctx.RegisterSyntaxNodeAction(
                c => Analyze(c, stringLocalizer),
                SyntaxKind.ElementAccessExpression
            );
        });
    }

    private static void Analyze(SyntaxNodeAnalysisContext context, INamedTypeSymbol stringLocalizer)
    {
        if (context.Node is not ElementAccessExpressionSyntax elementAccessExpression)
            return;

        if (
            context.SemanticModel.GetOperation(elementAccessExpression, context.CancellationToken)
            is not IPropertyReferenceOperation operation
        )
            return;

        if (
            operation.Instance?.Type is not INamedTypeSymbol instanceType
            || !SymbolEqualityComparer.Default.Equals(
                instanceType.OriginalDefinition,
                stringLocalizer.OriginalDefinition
            )
        )
            return;

        var firstArgument = operation.Arguments[0].Value;

        if (firstArgument.ConstantValue.Value is not string constantValue)
            return;

        if (instanceType.TypeArguments[0] is not INamedTypeSymbol resourceType)
            return;

        if (
            context.Compilation.GetTypeByMetadataName(
                $"{resourceType.ContainingNamespace}.{resourceType.Name}StringLocalizerExtensions"
            )
            is not INamedTypeSymbol extensionContainer
        )
            return;

        var targetExtensionParameterType = stringLocalizer.Construct(resourceType);

        if (
            extensionContainer
                .GetTypeMembers()
                .FirstOrDefault(m =>
                    SymbolEqualityComparer.Default.Equals(
                        m.ExtensionParameter?.Type,
                        targetExtensionParameterType
                    )
                )
            is not INamedTypeSymbol foundExtensions
        )
            return;

        var matchingMemberOrNull = foundExtensions
            .GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(p =>
                p.DeclaringSyntaxReferences.Any(s =>
                    s.GetSyntax()
                        is PropertyDeclarationSyntax
                        {
                            ExpressionBody.Expression: ElementAccessExpressionSyntax
                            {
                                ArgumentList.Arguments: var args
                            }
                        }
                    && args.Count == 1
                    && args[0].Expression is LiteralExpressionSyntax { Token.ValueText: var key }
                    && key == constantValue
                )
            );

        if (matchingMemberOrNull is null)
        {
            var properties = ImmutableDictionary.CreateBuilder<string, string?>();
            properties.Add("fileName", $"{resourceType.Name}.resx");
            properties.Add("key", constantValue);

            context.ReportDiagnostic(
                Diagnostic.Create(
                    s_addKeyToResourceFile,
                    context.Node.GetLocation(),
                    properties.ToImmutable(),
                    messageArgs: [constantValue, resourceType.Name]
                )
            );
        }
        else
        {
            var properties = ImmutableDictionary.CreateBuilder<string, string?>();
            properties.Add("memberName", matchingMemberOrNull.Name);

            context.ReportDiagnostic(
                Diagnostic.Create(
                    s_useStronglyTyped,
                    context.Node.GetLocation(),
                    properties.ToImmutable(),
                    messageArgs: [elementAccessExpression.Expression, matchingMemberOrNull.Name]
                )
            );
        }
    }
}
