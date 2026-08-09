using System.Diagnostics.CodeAnalysis;

namespace Pneuma.Localization.Resx.Generators.Helpers;

internal readonly record struct ParamCountResult(int? Count)
{
    [MemberNotNullWhen(true, nameof(Count))]
    [MemberNotNullWhen(false, nameof(InvalidReason))]
    public bool IsValid => InvalidReason is null;

    public string? InvalidReason { get; private init; }

    public static ParamCountResult Invalid(string reason) => new() { InvalidReason = reason };
}
