using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pneuma.Localization.Resx.Generators;

/// <summary>
///  Analyzes usages of `IStringLocalizer&lt;T&gt; element accessors to try to determine if we have
///  a strongly typed property for them to use instead. Also detects if a constant index argument
///  is missing from the corresponding resx file, to hint that they might be missing a resource
///  definition
/// </summary>
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
        "Add key '{0}' to {1}",
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
            // we'll reference this one a few times, so get it once for the whole compilation
            if (
                ctx.Compilation.GetTypeByMetadataName(
                    "Microsoft.Extensions.Localization.IStringLocalizer`1"
                )
                is not INamedTypeSymbol stringLocalizer
            )
                return;

            // find the file name attribute on the generated type, or exit if it's missing
            // (it shouldn't be missing)
            if (
                ctx.Compilation.GetTypeByMetadataName(
                    "Pneuma.Localization.GeneratedFromResxFileAttribute"
                )
                is not INamedTypeSymbol generatedFromResxAttribute
            )
                return;

            ctx.RegisterSyntaxNodeAction(
                c => Analyze(c, stringLocalizer, generatedFromResxAttribute),
                SyntaxKind.ElementAccessExpression
            );
        });
    }

    /// <summary>
    ///  Examines element access expressions to determine if they are on IStringLocalizer&lt;T&gt;
    ///  instances, if the argument is a constant string, and whether or not we have a generated
    ///  extension property for that argument. Emits an info diagnostic if we have the argument
    ///  to hint about the strongly typed property, and feeds a code fixer to use it automatically.
    ///  If we don't have the argument, emits a warning suggesting they add it to the culture
    ///  neutral file, which feeds a fixer which can do that for them too.
    /// </summary>
    /// <param name="context">Context used to analyze the source code</param>
    /// <param name="stringLocalizer">
    ///  <see cref="INamedTypeSymbol" /> representing an IStringLocalizer`1
    /// </param>
    /// <param name="generatedFromResxAttribute">
    ///  <see cref="INamedTypeSymbol" /> representing GeneratedFromResxFileAttribute
    /// </param>
    private static void Analyze(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol stringLocalizer,
        INamedTypeSymbol generatedFromResxAttribute
    )
    {
        // shouldn't happen, but let's be safe
        if (context.Node is not ElementAccessExpressionSyntax elementAccessExpression)
            return;

        if (
            context.SemanticModel.GetOperation(elementAccessExpression, context.CancellationToken)
            // TODO: implement methods for formatting strings, not just fixed values
            // for now this only works when you're not formatting the resulting string with
            // anything, so don't pretend like we can replace that at this point
            is not IPropertyReferenceOperation { Arguments.Length: 1 } operation
        )
            return;

        // ensure the operation instance type is IStringLocalizer<T>
        if (
            operation.Instance?.Type is not INamedTypeSymbol instanceType
            || !SymbolEqualityComparer.Default.Equals(
                instanceType.OriginalDefinition,
                stringLocalizer.OriginalDefinition
            )
        )
            return;

        // we already checked that there should only be one argument above, so just grab that one
        var firstArgument = operation.Arguments[0].Value;

        // if it's not a string constant, then we're done here;
        // intentionally not trying to check non-compile time constants as that gets complicated
        // and expands our scope quite a bit, and has a greater chance of false positives
        if (firstArgument.ConstantValue.Value is not string constantValue)
            return;

        // should be safe at this point, but just return in case something weird happens
        if (instanceType.TypeArguments[0] is not INamedTypeSymbol resourceType)
            return;

        // find the generated extensions, if they exist. exiting here when they don't is perfectly
        // fine, don't bother with diagnostics (my mind could be changed on this, but we'll see)
        if (
            context.Compilation.GetTypeByMetadataName(
                $"{resourceType.ContainingNamespace}.{resourceType.Name}StringLocalizerExtensions"
            )
            is not INamedTypeSymbol extensionContainer
        )
            return;

        // construct a bound generic type of IStringLocalizer<T> where T is the appropriate resource
        // type. this helps us find the correct nested type based on extension parameter,
        // in case there are more than one. the current shape of the generated extensions should
        // only have one nested type, but if that changes this shouldn't break automatically
        var targetExtensionParameterType = stringLocalizer.Construct(resourceType);

        // find the extension type member, or just exit if it't not there
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

        // get a property extension that uses the same key that we found in the user code
        var matchingMemberOrNull = foundExtensions
            .GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(p =>
                // have to dip out to the syntax model for simplicity here, it's harder to find
                // string constants from method bodies purely from the semantic model `Any` is used
                // because due to partial classes/members, technically a property could be declared
                // in multiple locations. rather than try to find which one we actually want to
                // check, just see if any of them help us out here
                p.DeclaringSyntaxReferences.Any(s =>
                    s.GetSyntax()
                        is PropertyDeclarationSyntax
                        {
                            ExpressionBody.Expression: ElementAccessExpressionSyntax
                            {
                                // currently only works with a single argument, this should be
                                // adjusted when we support formattable resources
                                ArgumentList.Arguments:
                                { Count: 1 } args
                            }
                        }
                    && args[0].Expression is LiteralExpressionSyntax { Token.ValueText: var key }
                    && key == constantValue
                )
            );

        if (matchingMemberOrNull is null)
        {
            if (
                extensionContainer
                    .GetAttributes()
                    .FirstOrDefault(a =>
                        SymbolEqualityComparer.Default.Equals(
                            a.AttributeClass,
                            generatedFromResxAttribute
                        )
                    )
                is not AttributeData attributeData
            )
                return;

            // this will be the relative path of the resx file
            if (attributeData.ConstructorArguments[0].Value is not string relativePath)
                return;

            // create the properties we want to attach to the diagnostic
            var properties = ImmutableDictionary.CreateBuilder<string, string?>();
            properties.Add("fileName", relativePath);
            properties.Add("key", constantValue);

            // and report
            context.ReportDiagnostic(
                Diagnostic.Create(
                    s_addKeyToResourceFile,
                    context.Node.GetLocation(),
                    properties.ToImmutable(),
                    messageArgs: [constantValue, relativePath]
                )
            );
        }
        else
        {
            // create the properties for the diagnostic
            var properties = ImmutableDictionary.CreateBuilder<string, string?>();
            properties.Add("memberName", matchingMemberOrNull.Name);
            properties.Add(
                "extensionContainer",
                extensionContainer.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
            );

            // reoprt
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
