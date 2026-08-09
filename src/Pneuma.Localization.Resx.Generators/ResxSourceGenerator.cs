using System.CodeDom.Compiler;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Pneuma.Localization.Resx.Generators.Helpers;
using Pneuma.Localization.Resx.Generators.Polyfills;

namespace Pneuma.Localization.Resx.Generators;

/// <summary>
///  Generates extension properties for IStringLocalizer&lt;T&gt; based on a resx file that should
///  correspond to the type
/// </summary>
[Generator]
public sealed class ResxSourceGenerator : IIncrementalGenerator
{
    private const string UnableToGenerateResxId = "RESX003";

    private static readonly DiagnosticDescriptor s_diagnostic = new(
        UnableToGenerateResxId,
        "Unable to generate source",
        "Unable to generate source: {0}",
        "Resource",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true
    );

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var resxFiles = context
            .AdditionalTextsProvider.Where(static file =>
            {
                // not a resx, then ignore, we onyl care about resx here
                if (Path.GetExtension(file.Path) != ".resx")
                    return false;

                // the rest of this just tries to determine if the file has a culture suffix,
                // e.g. Resource.en.resx
                var name = Path.GetFileNameWithoutExtension(file.Path);

                var lastDot = name.LastIndexOf('.');

                if (lastDot == -1)
                    return true;

                var maybeCulture = name.Substring(lastDot + 1);

                try
                {
                    // if we successfully resolved to a culture, then we're not interested
                    var culture = CultureInfo.GetCultureInfo(maybeCulture);
                    return false;
                }
                catch (CultureNotFoundException) { }

                return true;
            })
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(
                static (input, _) =>
                {
                    // exceptions thrown here should not happen "in real life" under normal
                    // compilation. if there are issues in real life, then i have work to do

                    var (file, options) = input;

                    // we'll need the root namespace as well as the project directory in order
                    // to make the rest of this project work the way it's intended to
                    if (
                        !options.GlobalOptions.TryGetValue(
                            "build_property.RootNamespace",
                            out var rootNamespace
                        ) || rootNamespace is null
                    )
                        return ResxFileInfo.Failure(
                            Diagnostic.Create(
                                s_diagnostic,
                                Location.None,
                                "unable to obtain root namespace"
                            )
                        );

                    if (
                        !options.GlobalOptions.TryGetValue(
                            "build_property.MSBuildProjectDirectory",
                            out var projectDirectory
                        ) || projectDirectory is null
                    )
                        return ResxFileInfo.Failure(
                            Diagnostic.Create(
                                s_diagnostic,
                                Location.None,
                                "unable to obtain project directory"
                            )
                        );

                    // should be `0`, but i'm not taking chances here
                    var index = file.Path.IndexOf(projectDirectory);

                    if (index == -1)
                        return ResxFileInfo.Failure(
                            Diagnostic.Create(s_diagnostic, Location.None, "no common path")
                        );

                    // this should be the path of the resx file relative to the csproj file
                    var relativePath = file.Path.Substring(index + projectDirectory.Length + 1);

                    var fileContent = file.GetText()?.ToString();

                    if (fileContent is null)
                        return ResxFileInfo.Failure(
                            Diagnostic.Create(
                                s_diagnostic,
                                Location.Create(file.Path, default, default),
                                "unable to read resx file"
                            )
                        );

                    // read it so we can find keys and values; if we can't read it, then once again,
                    // i'm not sure what we're supposed to do
                    using var stringReader = new StringReader(fileContent);

                    var xdoc = XDocument.Load(stringReader);

                    var root = xdoc.Root;

                    var resources = root.Elements("data")
                        .Select(e =>
                            (
                                (string)e.Attribute("name"),
                                (string)e.Element("value"),
                                (string?)e.Element("comment"),
                                FindParamCount((string)e.Element("value"))
                            )
                        )
                        .ToImmutableArray();

                    return new ResxFileInfo(
                        rootNamespace,
                        projectDirectory,
                        relativePath,
                        resources
                    );
                }
            );

        context.RegisterSourceOutput(
            context.CompilationProvider,
            static (ctx, input) =>
            {
                // adds an attribute to the source that the generated extension classes will use
                // doing it this way so that the referencing project's output will truly have no
                // dll with our name on it. we're just supposed to be in the shadows!

                ctx.AddSource(
                    "GeneratedFromResxFileAttribute.g.cs",
                    SourceText.From(GeneratedFromResxFileAttribute, Encoding.UTF8)
                );
            }
        );

        context.RegisterSourceOutput(
            resxFiles.Where(static f => !f.CanGenerate),
            static (ctx, input) => ctx.ReportDiagnostic(input.Diagnostic!)
        );

        context.RegisterSourceOutput(
            resxFiles.Where(f => f.CanGenerate).Combine(context.CompilationProvider),
            static (ctx, input) =>
            {
                var (resxInfo, compilation) = input;
                var (rootNamespace, projcetDirectory, relativePath, resources) = resxInfo;

                // ensuring these types exist before we generate source, otherwise this will result
                // in a build error
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

                // attempt to find the type name that is supposed to match this resource file
                // configured the way i've always seen it done, IStringLocalizer<T> should have a
                // corresponding class with the same name in a namespace that resembles the file's
                // path under the configured resource directory, however without asking the consumer
                // to provide that path directly, we have to do a "best guess" approach, which will
                // start with a namespace resembling the entire relative path with the root
                // namespace. for every check that fails, we remove a segment of the namespace
                // starting after the root namespace, until we have nothing left to check
                var candidateTypeName =
                    $"{resxInfo.RootNamespace}.{string.Join(".", Path.GetDirectoryName(resxInfo.RelativePath), Path.GetFileNameWithoutExtension(resxInfo.RelativePath)).Replace('\\', '.').Replace('/', '.')}";

                INamedTypeSymbol? resourceClassType;

                while (true)
                {
                    resourceClassType = compilation.GetTypeByMetadataName(candidateTypeName);

                    // if we found it, cool, break
                    if (resourceClassType is not null)
                        break;

                    // otherwise, gotta remove a segment
                    var dot = candidateTypeName.IndexOf('.', rootNamespace!.Length + 1);

                    // although if there aren't any more segments to remove, we didn't find it, and
                    // there aren't any more good guesses left
                    if (dot == -1)
                        break;

                    candidateTypeName =
                        $"{resxInfo.RootNamespace}.{candidateTypeName.Substring(dot + 1)}";
                }

                if (resourceClassType is null)
                {
                    if (Path.GetFileNameWithoutExtension(resxInfo.RelativePath) != "Program")
                        return;

                    // if we didn't find one, but the Resx file's name is `Program`, then we can
                    // use the type from the entrypoint of the compilation. this might be error
                    // prone, but we'll find out when this gets more usage
                    if (
                        compilation.GetEntryPoint(ctx.CancellationToken)?.ContainingType is
                        { Name: "Program", ContainingNamespace.IsGlobalNamespace: true } entryPoint
                    )
                        resourceClassType = entryPoint;
                    else
                        return;
                }

                var extensionSource = ExtensionSourceGenerator(
                    relativePath!,
                    resourceClassType,
                    stringLocalizer,
                    localizedString,
                    resources!.Value,
                    compilation
                );

                ctx.AddSource(
                    $"{resourceClassType.Name}StringLocalizerExtensions.g.cs",
                    SourceText.From(extensionSource, Encoding.UTF8)
                );
            }
        );
    }

    private static readonly FrozenDictionary<
        SanitizedMemberNameFailureReason,
        string
    > s_reasonText = new Dictionary<SanitizedMemberNameFailureReason, string>()
    {
        [SanitizedMemberNameFailureReason.None] = "",
        [SanitizedMemberNameFailureReason.Empty] = "Identifier was empty",
        [SanitizedMemberNameFailureReason.NotMeaningful] =
            "Generated identifier was not meaningful",
    }.ToFrozenDictionary();

    private static readonly FrozenSet<string> s_keywordTypes = new HashSet<string>()
    {
        "bool",
        "byte",
        "char",
        "decimal",
        "double",
        "float",
        "int",
        "long",
        "object",
        "sbyte",
        "short",
        "string",
        "uint",
        "ulong",
        "ushort",
    }.ToFrozenSet();

    /// <summary>
    ///  This attribute class will always look the same, so it can be declared as a constant
    /// </summary>
    private const string GeneratedFromResxFileAttribute = """
        // <auto-generated />
        #nullable enable

        namespace Pneuma.Localization;

        [global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false)]
        internal sealed class GeneratedFromResxFileAttribute(string path) : global::System.Attribute
        {
            public string Path => path;
        }

        """;

    /// <summary>
    ///  Based on all the info we&apos;ve found about the resx file, let&apos;s generate some code for it
    /// </summary>
    /// <param name="resourcePath">Relative path of the resx file</param>
    /// <param name="resourceClassType">Corresponding type for the generic IStringLocalizer</param>
    /// <param name="stringLocalizer">Named type symbol for IStringLocalizer</param>
    /// <param name="localizedString">Named type symbol for LocalizedString</param>
    /// <param name="resources">Resources in the resx file. Item1 is the key, Item2 is the value</param>
    /// <param name="compilation">Compilation for resolving argument types</param>
    /// <returns>Generated source code for IStringLocalizer&lt;T&gt; extension properties</returns>
    private static string ExtensionSourceGenerator(
        string resourcePath,
        INamedTypeSymbol resourceClassType,
        INamedTypeSymbol stringLocalizer,
        INamedTypeSymbol localizedString,
        ImmutableArray<(string, string, string?, ParamCountResult)> resources,
        Compilation compilation
    )
    {
        using var stringWriter = new StringWriter();
        using var indentedWriter = new IndentedTextWriter(stringWriter);

        // if the found type was public, this should be too, otherwise default to internal
        // this might have issues with private nested types, but we'll see if that happens in real
        // life. that feels like an odd use case to me anyway, but who knows what's gonna happen
        var accessibility = resourceClassType.DeclaredAccessibility switch
        {
            Accessibility.Public => SyntaxFactory.Token(SyntaxKind.PublicKeyword),
            _ => SyntaxFactory.Token(SyntaxKind.InternalKeyword),
        };

        indentedWriter.WriteLine("// <auto-generated />");
        indentedWriter.WriteLine("#nullable enable");
        indentedWriter.WriteLine();

        // omit the namespace declaration if it's meant to be in the global namespace
        if (resourceClassType.ContainingNamespace.IsGlobalNamespace)
            indentedWriter.WriteLine("// global namespace");
        else
            indentedWriter.WriteLine($"namespace {resourceClassType.ContainingNamespace};");

        indentedWriter.WriteLine();
        // try to hide this from editors, it's not meant to be used directly
        indentedWriter.WriteLine(
            "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]"
        );
        // easily lets us find the resx file from this type later
        indentedWriter.WriteLine(
            $"[global::Pneuma.Localization.GeneratedFromResxFile(\"{resourcePath}\")]"
        );
        indentedWriter.WriteLine(
            $"{accessibility} static class {resourceClassType.Name}StringLocalizerExtensions"
        );
        indentedWriter.WriteLine("{");

        indentedWriter.Indent++;
        // this is why this only works in c# 14+
        indentedWriter.WriteLine(
            $"extension({stringLocalizer.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}<{resourceClassType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}> localizer)"
        );
        indentedWriter.WriteLine("{");
        indentedWriter.Indent++;

        var writeExtraLine = false;

        foreach (var (key, value, comment, count) in resources)
        {
            // make sure the generated member name is safe to use as an identifier
            var memberName = SanitizeKeyForMemberName(key);
            var exampleText = TruncatedExampleStringText(value);

            if (memberName.CanGenerate && count.IsValid)
            {
                // extra spacing is nice sometimes
                if (writeExtraLine)
                    indentedWriter.WriteLineNoTabs("");

                indentedWriter.WriteLine("/// <summary>");
                indentedWriter.WriteLine(
                    // no telling what's in a resource's key, so let's make sure it doesn't interfere
                    // with xml docs
                    $"///  Gets a string like '{SecurityElement.Escape(exampleText)}' as a <see cref=\"{localizedString.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}\" />"
                );
                indentedWriter.WriteLine("/// </summary>");
                if (count.Count.Value > 0)
                {
                    var arguments = comment switch
                    {
                        { } c when c.StartsWith("Pneuma:") => ParseArgumentComment(
                            comment.Split([':'], 2)[1],
                            count.Count.Value,
                            compilation
                        ),
                        _ =>
                        [
                            .. Enumerable
                                .Range(0, count.Count.Value)
                                .Select(i => ("object", $"param{i}")),
                        ],
                    };

                    indentedWriter.WriteLine(
                        $"public {localizedString.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {memberName.Name}({string.Join(", ", arguments.Select(i => $"{i.Item1} {i.Item2}"))}) => localizer[\"{key}\", {string.Join(", ", arguments.Select(i => i.Item2))}];"
                    );
                }
                else
                    indentedWriter.WriteLine(
                        $"public {localizedString.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {memberName.Name} => localizer[\"{key}\"];"
                    );
            }
            else
            {
                indentedWriter.WriteLine(
                    $"// omitted property for resource `{key}` and value `{exampleText}`"
                );
                if (memberName.Reason is not SanitizedMemberNameFailureReason.None)
                    indentedWriter.WriteLine($"// Reason: {s_reasonText[memberName.Reason]}");
                if (!count.IsValid)
                    indentedWriter.WriteLine($"// Reason: {count.InvalidReason}");
                indentedWriter.WriteLine();
            }

            writeExtraLine = true;
        }
        indentedWriter.Indent--;
        indentedWriter.WriteLine("}");

        indentedWriter.Indent--;

        indentedWriter.WriteLine("}");

        return stringWriter.ToString();
    }

    /// <summary>
    ///  Based on the provided key, return a string value that should be safe to use as a member
    ///  identifier
    /// </summary>
    /// <param name="key">
    ///  Key from a resx file&apos;s resource indicated by the &quot;name&quot; attribute of a
    ///  &quot;data&quot; element
    /// </param>
    /// <returns>
    ///  A string that&apos;s safe to use as a member identifier, or `null` if
    ///  <paramref name="key"/> is invalid
    /// </returns>
    private static SanitizedMemberNameResult SanitizeKeyForMemberName(string key)
    {
        // if the key is effectively empty, we can't sanitize it
        if (string.IsNullOrWhiteSpace(key))
            return SanitizedMemberNameResult.Failure(SanitizedMemberNameFailureReason.Empty);

        // normalize so we can transform words with accented (but otherwise ascii) characters
        // into an approximation
        var normalized = key.Normalize(NormalizationForm.FormD);

        // stack allocating the entire possible space we might need to avoid multiple allocations
        // while constructing a string; allows us to do a single pass through the key and replace
        // as we see something invalid, with an optional spot for a prefixed underscore if necessary
        Span<char> charSpan = stackalloc char[normalized.Length + 1];

        // tracking result length specifically for easier construction, and slicing at the end
        var length = 0;

        var firstChar = true;

        for (var i = 0; i < normalized.Length; i++)
        {
            var current = normalized[i];

            // just skip diacritic enabling characters, no `_` necessary
            if (char.GetUnicodeCategory(current) == UnicodeCategory.NonSpacingMark)
                continue;

            // same as `Is(Not)Ascii`
            if (current > 127)
            {
                charSpan[length++] = '_';
                continue;
            }

            // identifiers aren't allowed to start with numbers, so prefix with an underscore
            if (length == 0 && char.IsDigit(current))
                charSpan[length++] = '_';

            if (char.IsLetterOrDigit(current))
            {
                if (firstChar)
                {
                    if (current is >= 'a' and <= 'z')
                        // when we know it's ascii, capitalizing is just a bitwise operation
                        charSpan[length++] = (char)(current & ~0x20);
                    else
                        charSpan[length++] = current;

                    firstChar = false;
                }
                else
                    charSpan[length++] = current;
            }
            else
                charSpan[length++] = '_';
        }

        // slicing to the determined length so we don't get anything trailing
        // Span<char>.ToString() just gets the string from the chars it contains
        var result = charSpan.Slice(0, length).ToString();

        // if we generated something that would just be a series of underscores, i don't consider
        // that to be usable as a member name
        if (string.IsNullOrWhiteSpace(result.Trim('_')))
            return SanitizedMemberNameResult.Failure(
                SanitizedMemberNameFailureReason.NotMeaningful
            );

        // otherwise we can return the generated string, leading/trailing underscores and all
        return new(result);
    }

    private static string TruncatedExampleStringText(string value)
    {
        const char Ellipsis = '\u2026';

        var newlineIndex = value.IndexOfAny(['\r', '\n']);

        if (newlineIndex is > 0 and <= 30)
            return $"{value.Substring(0, newlineIndex)}{Ellipsis}";

        if (value.Length > 30)
            return $"{value.Substring(0, 29)}{Ellipsis}";

        return value;
    }

    private static ParamCountResult FindParamCount(string value)
    {
        try
        {
            var compositeFormat = CompositeFormat.Parse(value);

            return new(compositeFormat.MinimumArgumentCount);
        }
        catch
        {
            return ParamCountResult.Invalid("Invalid format string");
        }
    }

    private static ImmutableArray<(string, string)> ParseArgumentComment(
        string comment,
        int expectedCount,
        Compilation compilation
    )
    {
        var args = comment.Split(';');

        var result = ImmutableArray.CreateBuilder<(string, string)>(expectedCount);

        for (var i = 0; i < args.Length && i < expectedCount; i++)
        {
            var current = args[i];

            var nameIndex = current.LastIndexOf(' ');

            var name = NormalizeArgumentName(current.Substring(nameIndex + 1).Trim());

            var type = current.Substring(0, nameIndex).Trim();

            if (!s_keywordTypes.Contains(type))
                if (compilation.GetTypeByMetadataName(type) is INamedTypeSymbol symbol)
                    type = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                else
                    type = "object";

            result.Add((type, name));
        }

        while (result.Count < expectedCount)
            result.Add(("object", $"param{result.Count}"));

        return result.ToImmutable();
    }

    private static string NormalizeArgumentName(string argumentName)
    {
        var normalized = argumentName.Normalize(NormalizationForm.FormD);

        Span<char> result = stackalloc char[normalized.Length];

        var length = 0;

        for (var i = 0; i < normalized.Length; i++)
        {
            var current = normalized[i];

            // just skip non-ascii characters
            if (current > 127)
                continue;

            if (length == 0 && char.IsDigit(current))
                result[length++] = '_';

            if (char.IsLetterOrDigit(current))
                result[length++] = current;
        }

        return result.ToString();
    }
}
