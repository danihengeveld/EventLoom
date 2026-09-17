using System.Reflection;

namespace EventLoom;

/// <summary>Base exception for invalid aggregate event-dispatch definitions or operations.</summary>
public class AggregateDispatchException(string message) : InvalidOperationException(message);

/// <summary>Indicates that an aggregate has no typed <c>Apply</c> method for an event.</summary>
public sealed class MissingApplyHandlerException(Type aggregateType, Type eventType)
    : AggregateDispatchException($"Aggregate '{aggregateType.FullName}' has no Apply handler for event '{eventType.FullName}'.");

/// <summary>Indicates that an aggregate has more than one typed <c>Apply</c> method for an event.</summary>
public sealed class AmbiguousApplyHandlerException(Type aggregateType, Type eventType)
    : AggregateDispatchException($"Aggregate '{aggregateType.FullName}' has multiple Apply handlers for event '{eventType.FullName}'.");

/// <summary>Indicates that a discovered aggregate <c>Apply</c> method violates dispatch requirements.</summary>
public sealed class InvalidApplyHandlerException(MethodInfo method, string reason)
    : AggregateDispatchException($"Apply handler '{method.DeclaringType?.FullName}.{method.Name}' is invalid: {reason}");
