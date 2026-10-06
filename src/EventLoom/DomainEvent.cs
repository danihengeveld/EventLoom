namespace EventLoom;

/// <summary>Marks a persisted domain event. Implement <see cref="IDomainEvent{TSelf, TAggregate}"/> instead.</summary>
public interface IDomainEvent;

/// <summary>
/// Identifies the aggregate that owns an event. Only <see cref="IDomainEvent{TSelf, TAggregate}"/> can implement
/// this interface, which guarantees that every owned event has a handler on its aggregate.
/// </summary>
/// <typeparam name="TAggregate">The aggregate that owns and applies the event.</typeparam>
public interface IDomainEvent<TAggregate> : IDomainEvent
    where TAggregate : Aggregate
{
    internal static abstract string PersistedName { get; }

    internal static abstract int PersistedVersion { get; }

    internal void ApplyTo(TAggregate aggregate);
}

/// <summary>
/// Declares an immutable domain event, its stable persisted identity, and its owning aggregate.
/// </summary>
/// <remarks>
/// The owning aggregate must implement <see cref="IApply{TEvent}"/> for the event; the compiler rejects
/// an event declaration whose aggregate cannot apply it.
/// </remarks>
/// <typeparam name="TSelf">The implementing event type.</typeparam>
/// <typeparam name="TAggregate">The aggregate that owns and applies the event.</typeparam>
public interface IDomainEvent<TSelf, TAggregate> : IDomainEvent<TAggregate>
    where TSelf : IDomainEvent<TSelf, TAggregate>
    where TAggregate : Aggregate, IApply<TSelf>
{
    /// <summary>Gets the stable persisted event name. Never change it after events have been stored.</summary>
    static abstract string EventType { get; }

    /// <summary>Gets the positive schema version. Defaults to 1; increase it together with an upcaster.</summary>
    static virtual int EventVersion => 1;

    static string IDomainEvent<TAggregate>.PersistedName => TSelf.EventType;

    static int IDomainEvent<TAggregate>.PersistedVersion => TSelf.EventVersion;

    void IDomainEvent<TAggregate>.ApplyTo(TAggregate aggregate) => aggregate.Apply((TSelf)this);
}

/// <summary>Applies one owned domain event to aggregate state.</summary>
/// <remarks>
/// Implement explicitly so the handler stays off the aggregate's public surface. Handlers must be deterministic
/// because they run again on every replay. Never call them directly; use <c>Raise</c>.
/// </remarks>
/// <typeparam name="TEvent">The handled event type.</typeparam>
public interface IApply<in TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>Applies the event to aggregate state.</summary>
    /// <param name="event">The event to apply.</param>
    void Apply(TEvent @event);
}
