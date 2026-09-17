using System.Reflection;

namespace EventLoom;

public class AggregateDispatchException(string message) : InvalidOperationException(message);

public sealed class MissingApplyHandlerException(Type aggregateType, Type eventType)
    : AggregateDispatchException($"Aggregate '{aggregateType.FullName}' has no Apply handler for event '{eventType.FullName}'.");

public sealed class AmbiguousApplyHandlerException(Type aggregateType, Type eventType)
    : AggregateDispatchException($"Aggregate '{aggregateType.FullName}' has multiple Apply handlers for event '{eventType.FullName}'.");

public sealed class InvalidApplyHandlerException(MethodInfo method, string reason)
    : AggregateDispatchException($"Apply handler '{method.DeclaringType?.FullName}.{method.Name}' is invalid: {reason}");
