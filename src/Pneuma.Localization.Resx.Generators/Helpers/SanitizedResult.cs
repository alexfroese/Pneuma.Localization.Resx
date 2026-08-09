using System.Diagnostics.CodeAnalysis;

namespace Pneuma.Localization.Resx.Generators.Helpers;

internal readonly record struct SanitizedMemberNameResult(string? Name)
{
    [MemberNotNullWhen(true, nameof(Name))]
    public bool CanGenerate => Name is not null;

    public SanitizedMemberNameFailureReason Reason { get; private init; } =
        SanitizedMemberNameFailureReason.None;

    public static SanitizedMemberNameResult Failure(SanitizedMemberNameFailureReason reason) =>
        new() { Reason = reason };
}

internal enum SanitizedMemberNameFailureReason
{
    None,
    Empty,
    NotMeaningful,
}
