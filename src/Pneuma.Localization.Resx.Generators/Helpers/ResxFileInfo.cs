using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Pneuma.Localization.Resx.Generators.Helpers;

internal readonly record struct ResxFileInfo(
    string? RootNamespace,
    string? ProjectDirectory,
    string? RelativePath,
    ImmutableArray<(string key, string value, string? comment, ParamCountResult)>? Resources
)
{
    [MemberNotNullWhen(
        true,
        nameof(RootNamespace),
        nameof(ProjectDirectory),
        nameof(RelativePath),
        nameof(Resources)
    )]
    [MemberNotNullWhen(false, nameof(Diagnostic))]
    public bool CanGenerate => Diagnostic is null;

    public Diagnostic? Diagnostic { get; private init; }

    public static ResxFileInfo Failure(Diagnostic diagnostic) => new() { Diagnostic = diagnostic };
}
