namespace EventLoom;

/// <summary>Base exception for invalid aggregate event-dispatch definitions or operations.</summary>
public class AggregateDispatchException(string message) : InvalidOperationException(message);

/// <summary>Indicates that an aggregate received an event owned by another aggregate type.</summary>
public sealed class EventOwnershipException(Type aggregateType, Type eventType)
    : AggregateDispatchException(
        $"Event '{eventType.FullName}' is not owned by aggregate '{aggregateType.FullName}'.");

/// <summary>Indicates that an aggregate does not pass itself as the <c>TSelf</c> type argument.</summary>
public sealed class InvalidAggregateTypeException(Type aggregateType, Type declaredSelfType)
    : AggregateDispatchException(
        $"Aggregate '{aggregateType.FullName}' must derive from Aggregate<{aggregateType.Name}, TId>, " +
        $"not Aggregate<{declaredSelfType.Name}, TId>.");

/// <summary>
/// Indicates that <c>Raise</c> was called while an event handler or snapshot callback was running.
/// Handlers run again on every replay, so they must not raise events.
/// </summary>
public sealed class NestedRaiseException(Type aggregateType)
    : AggregateDispatchException(
        $"Aggregate '{aggregateType.FullName}' raised an event from an Apply or snapshot callback. " +
        "Raise events only from command methods.");

/// <summary>Indicates that an aggregate factory returned an aggregate that already has state or pending events.</summary>
public sealed class AggregateFactoryException(Type aggregateType)
    : AggregateDispatchException(
        $"The factory for aggregate '{aggregateType.FullName}' returned an aggregate with applied or pending events. " +
        "Aggregate constructors must not raise events.");
