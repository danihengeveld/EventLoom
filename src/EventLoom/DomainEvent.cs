using System.Collections.Concurrent;

namespace EventLoom;

/// <summary>Marks an immutable event raised by a specific domain aggregate.</summary>
/// <typeparam name="TAggregate">The aggregate that owns and applies the event.</typeparam>
public interface IDomainEvent<out TAggregate>
    where TAggregate : Aggregate;

internal static class DomainEventContract
{
    private static readonly Type OpenContract = typeof(IDomainEvent<>);
    private static readonly ConcurrentDictionary<Type, EventOwner> Owners = new();

    public static bool IsEvent(Type type) => GetAggregateType(type) is not null;

    public static Type? GetAggregateType(Type type) =>
        Owners.GetOrAdd(type, static eventType => new EventOwner(FindAggregateType(eventType))).AggregateType;

    private static Type? FindAggregateType(Type type)
    {
        var aggregateTypes = type.GetInterfaces()
            .Where(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == OpenContract)
            .Select(candidate => candidate.GetGenericArguments()[0])
            .Distinct()
            .ToArray();
        return aggregateTypes.Length == 1 ? aggregateTypes[0] : null;
    }

    private readonly record struct EventOwner(Type? AggregateType);
}

/// <summary>Associates a stable persisted name and schema version with a domain event.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
public sealed class EventTypeAttribute(string name) : Attribute
{
    /// <summary>Gets the stable persisted event name.</summary>
    public string Name { get; } = ValidateName(name);

    /// <summary>Gets or sets the positive schema version.</summary>
    public int Version
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Event schema version must be positive.");
    } = 1;

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }
}
