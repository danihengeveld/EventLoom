

namespace EventLoom.Analyzers;

/// <summary>The stable identifiers of the EventLoom analyzer diagnostics.</summary>
public static class DiagnosticIds
{
    public const string InvalidSelfType = "EL0101";
    public const string RaiseInHandler = "EL0102";
    public const string RaiseInConstructor = "EL0103";
    public const string DirectHandlerCall = "EL0104";
    public const string NondeterministicHandler = "EL0105";
    public const string NonConstantIdentity = "EL0106";
    public const string DuplicateIdentity = "EL0107";
    public const string MutableContract = "EL0108";
    public const string ForeignApplyHandler = "EL0109";
    public const string StateChangedOutsideApply = "EL0110";
}
