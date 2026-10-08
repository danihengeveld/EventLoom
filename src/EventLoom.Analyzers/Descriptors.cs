using Microsoft.CodeAnalysis;

namespace EventLoom.Analyzers;

internal static class Descriptors
{
    private const string Category = "EventLoom";

    internal static readonly DiagnosticDescriptor InvalidSelfType = new(
        DiagnosticIds.InvalidSelfType,
        "Aggregate must pass itself as TSelf",
        "Aggregate '{0}' must derive from Aggregate<{0}, TId>, not Aggregate<{1}, TId>",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Concrete aggregates pass themselves as TSelf; abstract generic bases forward their own TSelf type parameter.");

    internal static readonly DiagnosticDescriptor RaiseInHandler = new(
        DiagnosticIds.RaiseInHandler,
        "Do not raise events from Apply or snapshot callbacks",
        "'{0}' raises an event from an Apply or snapshot callback",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Handlers run again on every replay. Raising from them duplicates events; raise only from command methods.");

    internal static readonly DiagnosticDescriptor RaiseInConstructor = new(
        DiagnosticIds.RaiseInConstructor,
        "Do not raise events from aggregate constructors",
        "Constructor of '{0}' raises an event",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The aggregate factory runs on every load. Raise creation events from a command method instead.");

    internal static readonly DiagnosticDescriptor DirectHandlerCall = new(
        DiagnosticIds.DirectHandlerCall,
        "Do not call Apply or snapshot callbacks directly",
        "Direct call to '{0}' changes aggregate state without recording an event",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Use Raise to apply events. EventLoom invokes snapshot callbacks when saving and loading.");

    internal static readonly DiagnosticDescriptor NondeterministicHandler = new(
        DiagnosticIds.NondeterministicHandler,
        "Apply and snapshot callbacks must be deterministic",
        "'{0}' uses non-deterministic '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Replay must reproduce the same state. Put clocks, random values, identifiers, and I/O results in the event instead.");

    internal static readonly DiagnosticDescriptor NonConstantIdentity = new(
        DiagnosticIds.NonConstantIdentity,
        "Persisted identity must be a valid constant",
        "'{0}.{1}' must return {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Event and snapshot names and versions are persisted; they must be compile-time constants with valid values.");

    internal static readonly DiagnosticDescriptor DuplicateIdentity = new(
        DiagnosticIds.DuplicateIdentity,
        "Persisted identity is declared more than once",
        "{0} type '{1}' is declared by multiple types: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Each persisted event or snapshot name maps to exactly one type. Model schema changes with versions and upcasters.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    internal static readonly DiagnosticDescriptor MutableContract = new(
        DiagnosticIds.MutableContract,
        "Events and snapshots must be immutable",
        "'{0}' declares mutable member '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Use init-only properties and readonly fields so persisted history cannot change after it is raised.");

    internal static readonly DiagnosticDescriptor ForeignApplyHandler = new(
        DiagnosticIds.ForeignApplyHandler,
        "Apply handler is never used",
        "Aggregate '{0}' handles '{1}', which it does not own",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "EventLoom only dispatches events owned by the aggregate through IDomainEvent<TSelf, TAggregate>.");

    internal static readonly DiagnosticDescriptor StateChangedOutsideApply = new(
        DiagnosticIds.StateChangedOutsideApply,
        "Change aggregate state only in Apply handlers",
        "'{0}' assigns '{1}' outside an Apply or RestoreSnapshot callback",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: false,
        description: "State changed outside handlers is lost on replay. Raise an event and change state in its handler.");
}
