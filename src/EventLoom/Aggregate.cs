using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;

namespace EventLoom;

/// <summary>
/// Non-generic base type for aggregate ownership metadata.
/// </summary>
public abstract class Aggregate;

/// <summary>
/// Base type for an event-sourced aggregate with a stable application-defined identifier.
/// </summary>
/// <typeparam name="TId">The aggregate identifier type.</typeparam>
public abstract class Aggregate<TId> : Aggregate
{
    private static readonly ConcurrentDictionary<Type, AggregateDispatcher> Dispatchers = new();
    private readonly List<PendingEvent> pendingEvents = [];
    private readonly ReadOnlyCollection<PendingEvent> readOnlyPendingEvents;

    /// <summary>
    /// Initializes an aggregate with its identifier.
    /// </summary>
    /// <param name="id">The application-defined aggregate identifier.</param>
    protected Aggregate(TId id)
    {
        Id = id;
        readOnlyPendingEvents = pendingEvents.AsReadOnly();
    }

    /// <summary>Gets the aggregate identifier.</summary>
    public TId Id { get; }

    /// <summary>Gets the version after all applied persisted and pending events.</summary>
    public long Version { get; private set; }

    /// <summary>Gets the events raised since the last successful non-idempotent save.</summary>
    public IReadOnlyList<PendingEvent> PendingEvents => readOnlyPendingEvents;

    /// <summary>
    /// Raises and immediately applies a new domain event.
    /// </summary>
    /// <typeparam name="TEvent">The concrete event type.</typeparam>
    /// <param name="event">The event representing the state transition.</param>
    protected void Raise<TEvent>(TEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ApplyEvent(@event);
        pendingEvents.Add(new PendingEvent(@event, Version));
    }

    /// <summary>
    /// Replays historical events without adding them to the pending collection.
    /// </summary>
    /// <param name="history">The events to replay in stream-version order.</param>
    protected void Replay(IEnumerable<object> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        foreach (var @event in history)
        {
            ArgumentNullException.ThrowIfNull(@event);
            ApplyEvent(@event);
        }
    }

    /// <summary>
    /// Applies persisted history without adding events to the pending collection.
    /// </summary>
    public void ApplyHistory(IEnumerable<object> history) => Replay(history);

    /// <summary>Gets the events raised since the last successful non-idempotent save.</summary>
    public IReadOnlyList<PendingEvent> GetPendingEvents() => PendingEvents;

    /// <summary>Removes all pending events after they have been persisted.</summary>
    public void ClearPendingEvents() => pendingEvents.Clear();

    internal void RestoreSnapshotVersion(long streamVersion)
    {
        if (streamVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamVersion));
        }

        Version = streamVersion;
        pendingEvents.Clear();
    }

    private void ApplyEvent(object @event)
    {
        var owner = DomainEventContract.GetAggregateType(@event.GetType());
        if (owner is null || !owner.IsAssignableFrom(GetType()))
        {
            throw new EventOwnershipException(GetType(), @event.GetType());
        }

        var dispatcher = Dispatchers.GetOrAdd(GetType(), static type => AggregateDispatcher.Create(type));
        dispatcher.Apply(this, @event);
        Version++;
    }

    /// <summary>Describes a pending event and its aggregate version after application.</summary>
    /// <param name="Event">The raised domain event.</param>
    /// <param name="StreamVersion">The aggregate version after applying the event.</param>
    public sealed record PendingEvent(object Event, long StreamVersion);

    private sealed class AggregateDispatcher
    {
        private readonly IReadOnlyDictionary<Type, Action<object, object>> handlers;

        private AggregateDispatcher(IReadOnlyDictionary<Type, Action<object, object>> handlers)
        {
            this.handlers = handlers;
        }

        public static AggregateDispatcher Create(Type aggregateType)
        {
            var methods = aggregateType
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "Apply");
            var discovered = new Dictionary<Type, Action<object, object>>();

            foreach (var method in methods)
            {
                if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters)
                {
                    throw new InvalidApplyHandlerException(method, "handlers must be non-static, non-generic, and concrete");
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 1 || !DomainEventContract.IsEvent(parameters[0].ParameterType))
                {
                    throw new InvalidApplyHandlerException(
                        method,
                        "handlers must accept one IDomainEvent<TAggregate> parameter");
                }

                if (method.ReturnType != typeof(void))
                {
                    throw new InvalidApplyHandlerException(method, "handlers must return void");
                }

                if (!(method.IsPrivate ||
                      method.IsFamily ||
                      method.IsFamilyAndAssembly ||
                      method.IsFamilyOrAssembly))
                {
                    throw new InvalidApplyHandlerException(method, "handlers must be private or protected");
                }

                var eventType = parameters[0].ParameterType;
                if (!discovered.TryAdd(eventType, CreateDelegate(method, eventType)))
                {
                    throw new AmbiguousApplyHandlerException(aggregateType, eventType);
                }
            }

            return new AggregateDispatcher(discovered);
        }

        public void Apply(object aggregate, object @event)
        {
            if (!handlers.TryGetValue(@event.GetType(), out var handler))
            {
                throw new MissingApplyHandlerException(aggregate.GetType(), @event.GetType());
            }

            handler(aggregate, @event);
        }

        private static Action<object, object> CreateDelegate(MethodInfo method, Type eventType)
        {
            var aggregate = Expression.Parameter(typeof(object), "aggregate");
            var @event = Expression.Parameter(typeof(object), "event");
            var call = Expression.Call(
                Expression.Convert(aggregate, method.DeclaringType!),
                method,
                Expression.Convert(@event, eventType));
            return Expression.Lambda<Action<object, object>>(call, aggregate, @event).Compile();
        }
    }
}
