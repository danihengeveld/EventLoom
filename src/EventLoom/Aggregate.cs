using System.Collections.ObjectModel;

namespace EventLoom;

/// <summary>
/// Non-generic base type for aggregate ownership metadata.
/// </summary>
public abstract class Aggregate
{
    private protected Aggregate()
    {
    }

    /// <summary>Gets whether an event handler or snapshot callback is currently running.</summary>
    private protected bool IsApplying { get; private set; }

    /// <summary>Runs an event handler or snapshot callback while rejecting nested <c>Raise</c> calls.</summary>
    private protected void RunGuarded(Action action)
    {
        if (IsApplying)
        {
            throw new NestedRaiseException(GetType());
        }

        IsApplying = true;
        try
        {
            action();
        }
        finally
        {
            IsApplying = false;
        }
    }

    /// <summary>Runs a snapshot capture callback while rejecting nested <c>Raise</c> calls.</summary>
    internal TSnapshot CaptureSnapshot<TSnapshot>()
    {
        if (this is not ISnapshotable<TSnapshot> snapshotable)
        {
            throw new InvalidOperationException(
                $"Aggregate '{GetType().FullName}' does not implement ISnapshotable<{typeof(TSnapshot).Name}>.");
        }

        TSnapshot snapshot = default!;
        RunGuarded(() => snapshot = snapshotable.CreateSnapshot());
        return snapshot;
    }
}

/// <summary>
/// Base type for an event-sourced aggregate with a stable application-defined identifier.
/// </summary>
/// <remarks>
/// Pass the aggregate itself as <typeparamref name="TSelf"/>: <c>sealed class Order : Aggregate&lt;Order, Guid&gt;</c>.
/// Implement <see cref="IApply{TEvent}"/> for every owned event.
/// </remarks>
/// <typeparam name="TSelf">The concrete aggregate type.</typeparam>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public abstract class Aggregate<TSelf, TId> : Aggregate
    where TSelf : Aggregate<TSelf, TId>
{
    private readonly List<PendingEvent> pendingEvents = [];
    private readonly ReadOnlyCollection<PendingEvent> readOnlyPendingEvents;

    /// <summary>
    /// Initializes an aggregate with its identifier.
    /// </summary>
    /// <param name="id">The application-defined aggregate identifier.</param>
    /// <exception cref="InvalidAggregateTypeException">The aggregate does not pass itself as <typeparamref name="TSelf"/>.</exception>
    protected Aggregate(TId id)
    {
        if (this is not TSelf)
        {
            throw new InvalidAggregateTypeException(GetType(), typeof(TSelf));
        }

        Id = id;
        readOnlyPendingEvents = pendingEvents.AsReadOnly();
    }

    /// <summary>Gets the aggregate identifier.</summary>
    public TId Id { get; }

    /// <summary>Gets the version after all applied persisted and pending events.</summary>
    public long Version { get; private set; }

    /// <summary>Gets the events raised since the last successful non-idempotent save.</summary>
    internal IReadOnlyList<PendingEvent> PendingEvents => readOnlyPendingEvents;

    /// <summary>Gets whether no event has been applied or raised since construction.</summary>
    internal bool IsPristine => Version == 0 && pendingEvents.Count == 0;

    private TSelf Self => (TSelf)this;

    /// <summary>
    /// Raises and immediately applies a new domain event owned by this aggregate.
    /// </summary>
    /// <typeparam name="TEvent">The concrete event type.</typeparam>
    /// <param name="event">The event representing the state transition.</param>
    /// <exception cref="NestedRaiseException">Called from an event handler or snapshot callback.</exception>
    protected void Raise<TEvent>(TEvent @event)
        where TEvent : IDomainEvent<TSelf>
    {
        ArgumentNullException.ThrowIfNull(@event);
        ApplyOwned(@event);
        pendingEvents.Add(new PendingEvent(@event, Version));
    }

    /// <summary>
    /// Replays historical events without adding them to the pending collection.
    /// </summary>
    /// <param name="history">The events to replay in stream-version order.</param>
    /// <exception cref="EventOwnershipException">An event is not owned by this aggregate.</exception>
    protected void Replay(IEnumerable<object> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        foreach (var @event in history)
        {
            ArgumentNullException.ThrowIfNull(@event);
            if (@event is not IDomainEvent<TSelf> owned)
            {
                throw new EventOwnershipException(GetType(), @event.GetType());
            }

            ApplyOwned(owned);
        }
    }

    /// <summary>
    /// Applies persisted history without adding events to the pending collection.
    /// </summary>
    internal void ApplyHistory(IEnumerable<object> history) => Replay(history);

    /// <summary>Removes all pending events after they have been persisted.</summary>
    internal void ClearPendingEvents() => pendingEvents.Clear();

    /// <summary>Restores snapshot state and stream version while rejecting nested <c>Raise</c> calls.</summary>
    internal void RestoreSnapshot<TSnapshot>(TSnapshot snapshot, long streamVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(streamVersion);
        if (this is not ISnapshotable<TSnapshot> snapshotable)
        {
            throw new InvalidOperationException(
                $"Aggregate '{GetType().FullName}' does not implement ISnapshotable<{typeof(TSnapshot).Name}>.");
        }

        RunGuarded(() => snapshotable.RestoreSnapshot(snapshot));
        Version = streamVersion;
        pendingEvents.Clear();
    }

    private void ApplyOwned(IDomainEvent<TSelf> @event)
    {
        var self = Self;
        RunGuarded(() => @event.ApplyTo(self));
        Version++;
    }

    /// <summary>Describes a pending event and its aggregate version after application.</summary>
    /// <param name="Event">The raised domain event.</param>
    /// <param name="StreamVersion">The aggregate version after applying the event.</param>
    internal sealed record PendingEvent(object Event, long StreamVersion);
}
