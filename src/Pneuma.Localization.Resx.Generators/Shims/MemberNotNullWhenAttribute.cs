#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace System.Diagnostics.CodeAnalysis;

#pragma warning restore IDE0130 // Namespace does not match folder structure

[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Method,
    Inherited = false,
    AllowMultiple = true
)]
public sealed class MemberNotNullWhenAttribute : Attribute
{
    public MemberNotNullWhenAttribute(bool returnValue, string member)
    {
        ReturnValue = returnValue;
        Members = [member];
    }

    public MemberNotNullWhenAttribute(bool returnValue, params string[] members)
    {
        ReturnValue = returnValue;
        Members = members;
    }

    public bool ReturnValue { get; }
    public string[] Members { get; }
}
