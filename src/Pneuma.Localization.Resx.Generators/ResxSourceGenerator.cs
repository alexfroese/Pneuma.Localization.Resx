using System.CodeDom.Compiler;
using System.Collections.Immutable;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Pneuma.Localization.Resx.Generators;

[Generator]
public sealed class ResxSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var resxFiles = context
            .AdditionalTextsProvider.Where(file =>
            {
                if (Path.GetExtension(file.Path) != ".resx")
                    return false;

                var name = Path.GetFileNameWithoutExtension(file.Path);

                var lastDot = name.LastIndexOf('.');

                if (lastDot == -1)
                    return true;

                var maybeCulture = name.Substring(lastDot + 1);

                try
                {
                    var culture = CultureInfo.GetCultureInfo(maybeCulture);
                    return false;
                }
                catch (CultureNotFoundException) { }

                return true;
            })
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(
                (input, _) =>
                {
                    var (file, options) = input;

                    if (
                        !options.GlobalOptions.TryGetValue(
                            "build_property.RootNamespace",
                            out var rootNamespace
                        ) || rootNamespace is null
                    )
                        throw new InvalidOperationException("unable to obtain namespace");

                    if (
                        !options.GlobalOptions.TryGetValue(
                            "build_property.MSBuildProjectDirectory",
                            out var projectDirectory
                        ) || projectDirectory is null
                    )
                        throw new InvalidOperationException("unable to obtain project directory");

                    var index = file.Path.IndexOf(projectDirectory);

                    if (index == -1)
                        throw new InvalidOperationException("no common path");

                    var relativePath = file.Path.Substring(index + projectDirectory.Length + 1);

                    using var stringReader = new StringReader(
                        file.GetText()?.ToString()
                            ?? throw new InvalidOperationException("unable to read resx file")
                    );

                    var xdoc = XDocument.Load(stringReader);

                    var root = xdoc.Root;

                    var resources = root.Elements("data")
                        .Select(e => ((string)e.Attribute("name"), (string)e.Element("value")))
                        .ToImmutableArray();

                    return (rootNamespace, projectDirectory, relativePath, resources);
                }
            );

        context.RegisterSourceOutput(
            context.CompilationProvider,
            (ctx, input) =>
            {
                var source = GeneratedResxAttribute();

                ctx.AddSource(
                    "GeneratedFromResxFileAttribute.g.cs",
                    SourceText.From(source, Encoding.UTF8)
                );
            }
        );

        context.RegisterSourceOutput(
            resxFiles.Combine(context.CompilationProvider),
            (ctx, input) =>
            {
                var ((rootNamespace, projectDirectory, relativePath, resources), compilation) =
                    input;

                if (
                    compilation.GetTypeByMetadataName(
                        "Microsoft.Extensions.Localization.IStringLocalizer"
                    )
                    is not INamedTypeSymbol stringLocalizer
                )
                    return;

                if (
                    compilation.GetTypeByMetadataName(
                        "Microsoft.Extensions.Localization.LocalizedString"
                    )
                    is not INamedTypeSymbol localizedString
                )
                    return;

                var candidateTypeName =
                    $"{rootNamespace}.{string.Join(".", Path.GetDirectoryName(relativePath), Path.GetFileNameWithoutExtension(relativePath)).Replace('\\', '.').Replace('/', '.')}";

                INamedTypeSymbol? resourceClassType;

                while (true)
                {
                    resourceClassType = compilation.GetTypeByMetadataName(candidateTypeName);

                    if (resourceClassType is not null)
                        break;

                    var dot = candidateTypeName.IndexOf('.', rootNamespace.Length + 1);

                    if (dot == -1)
                        break;

                    candidateTypeName = $"{rootNamespace}.{candidateTypeName.Substring(dot + 1)}";
                }

                if (resourceClassType is null)
                {
                    if (Path.GetFileNameWithoutExtension(relativePath) != "Program")
                        return;

                    if (
                        compilation.GetEntryPoint(ctx.CancellationToken)?.ContainingType is
                        { Name: "Program", ContainingNamespace.IsGlobalNamespace: true } entryPoint
                    )
                        resourceClassType = entryPoint;
                    else
                        return;
                }

                var extensionSource = ExtensionSourceGenerator(
                    relativePath,
                    resourceClassType,
                    stringLocalizer,
                    localizedString,
                    resources
                );

                ctx.AddSource(
                    $"{resourceClassType.Name}StringLocalizerExtensions.g.cs",
                    SourceText.From(extensionSource, Encoding.UTF8)
                );
            }
        );
    }

    private static string GeneratedResxAttribute() =>
        """
            // <auto-generated />
            #nullable enable

            namespace Pneuma.Localization;

            [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false)]
            internal sealed class GeneratedFromResxFileAttribute(string path) : global::System.Attribute
            {
                public string Path => path;
            }

            """;

    private static string ExtensionSourceGenerator(
        string resourcePath,
        INamedTypeSymbol resourceClassType,
        INamedTypeSymbol stringLocalizer,
        INamedTypeSymbol localizedString,
        ImmutableArray<(string, string)> resources
    )
    {
        using var stringWriter = new StringWriter();
        using var indentedWriter = new IndentedTextWriter(stringWriter);

        var accessibility = resourceClassType.DeclaredAccessibility switch
        {
            Accessibility.Public => SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            _ => SyntaxFactory.Token(SyntaxKind.InternalKeyword),
        };

        indentedWriter.WriteLine("// <auto-generated />");
        indentedWriter.WriteLine("#nullable enable");
        indentedWriter.WriteLine();

        if (resourceClassType.ContainingNamespace.IsGlobalNamespace)
            indentedWriter.WriteLine("// global namespace");
        else
            indentedWriter.WriteLine($"namespace {resourceClassType.ContainingNamespace};");

        indentedWriter.WriteLine();
        indentedWriter.WriteLine(
            "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]"
        );
        indentedWriter.WriteLine(
            $"[global::Pneuma.Localization.GeneratedFromResxFile(\"{resourcePath}\")]"
        );
        indentedWriter.WriteLine(
            $"{accessibility} static class {resourceClassType.Name}StringLocalizerExtensions"
        );
        indentedWriter.WriteLine("{");

        indentedWriter.Indent++;
        indentedWriter.WriteLine(
            $"extension({stringLocalizer.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}<{resourceClassType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}> localizer)"
        );
        indentedWriter.WriteLine("{");
        indentedWriter.Indent++;

        var writeExtraLine = false;

        foreach (var (key, value) in resources)
        {
            if (writeExtraLine)
                indentedWriter.WriteLineNoTabs("");

            indentedWriter.WriteLine("/// <summary>");
            indentedWriter.WriteLine(
                $"///  Gets a string like '{SecurityElement.Escape(value)}' as a <see cref=\"{localizedString.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}\" />"
            );
            indentedWriter.WriteLine("/// </summary>");
            indentedWriter.WriteLine(
                $"public {localizedString.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {SafeMemberName(key)} => localizer[\"{key}\"];"
            );

            writeExtraLine = true;
        }
        indentedWriter.Indent--;
        indentedWriter.WriteLine("}");

        indentedWriter.Indent--;

        indentedWriter.WriteLine("}");

        return stringWriter.ToString();
    }

    private static string SafeMemberName(string key)
    {
        // stack allocating the entire possible space we might need to avoid multiple allocations
        // while constructing a string; allows us to do a single pass through the key and replace
        // as we see something invalid, with an optional spot for a prefixed underscore if necessary
        Span<char> result = stackalloc char[key.Length + 1];

        // tracking result length specifically for easier construction, and slicing at the end
        var length = 0;

        for (var i = 0; i < key.Length; i++)
        {
            var current = key[i];

            // same as `IsAscii`
            if (current > 127)
            {
                result[length++] = '_';
                continue;
            }

            // identifiers aren't allowed to start with numbers, so prefix with an underscore
            if (i == 0 && char.IsDigit(current))
                result[length++] = '_';

            if (char.IsLetterOrDigit(current))
                result[length++] = current;
            else
                result[length++] = '_';
        }

        // slicing to the determined length so we don't get anything trailing
        // Span<char>.ToString() just gets the string from the chars it contains
        return result.Slice(0, length).ToString();
    }
}
