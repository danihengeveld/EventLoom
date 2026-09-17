using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;

namespace EventLoom;

public abstract class Aggregate<TId>
{
    private static readonly ConcurrentDictionary<Type, AggregateDispatcher> Dispatchers = new();
    private readonly List<PendingEvent> pendingEvents = [];
    private readonly ReadOnlyCollection<PendingEvent> readOnlyPendingEvents;

    protected Aggregate(TId id)
    {
        Id = id;
        readOnlyPendingEvents = pendingEvents.AsReadOnly();
    }

    public TId Id { get; }

    public long Version { get; private set; }

    public IReadOnlyList<PendingEvent> PendingEvents => readOnlyPendingEvents;

    protected void Raise<TEvent>(TEvent @event)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        ApplyEvent(@event);
        pendingEvents.Add(new PendingEvent(@event, Version));
    }

    protected void Replay(IEnumerable<IDomainEvent> history)
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
    public void ApplyHistory(IEnumerable<IDomainEvent> history) => Replay(history);

    public IReadOnlyList<PendingEvent> GetPendingEvents() => PendingEvents;

    public void ClearPendingEvents() => pendingEvents.Clear();

    private void ApplyEvent(IDomainEvent @event)
    {
        var dispatcher = Dispatchers.GetOrAdd(GetType(), static type => AggregateDispatcher.Create(type));
        dispatcher.Apply(this, @event);
        Version++;
    }

    public sealed record PendingEvent(IDomainEvent Event, long StreamVersion);

    private sealed class AggregateDispatcher
    {
        private readonly IReadOnlyDictionary<Type, Action<object, IDomainEvent>> handlers;

        private AggregateDispatcher(IReadOnlyDictionary<Type, Action<object, IDomainEvent>> handlers)
        {
            this.handlers = handlers;
        }

        public static AggregateDispatcher Create(Type aggregateType)
        {
            var methods = aggregateType
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "Apply");
            var discovered = new Dictionary<Type, Action<object, IDomainEvent>>();

            foreach (var method in methods)
            {
                if (method.IsStatic || method.IsAbstract || method.ContainsGenericParameters)
                {
                    throw new InvalidApplyHandlerException(method, "handlers must be non-static, non-generic, and concrete");
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 1 || !typeof(IDomainEvent).IsAssignableFrom(parameters[0].ParameterType))
                {
                    throw new InvalidApplyHandlerException(method, "handlers must accept one IDomainEvent parameter");
                }

                if (method.ReturnType != typeof(void))
                {
                    throw new InvalidApplyHandlerException(method, "handlers must return void");
                }

                if (method.IsPublic)
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

        public void Apply(object aggregate, IDomainEvent @event)
        {
            if (!handlers.TryGetValue(@event.GetType(), out var handler))
            {
                throw new MissingApplyHandlerException(aggregate.GetType(), @event.GetType());
            }

            handler(aggregate, @event);
        }

        private static Action<object, IDomainEvent> CreateDelegate(MethodInfo method, Type eventType)
        {
            var aggregate = Expression.Parameter(typeof(object), "aggregate");
            var @event = Expression.Parameter(typeof(IDomainEvent), "event");
            var call = Expression.Call(
                Expression.Convert(aggregate, method.DeclaringType!),
                method,
                Expression.Convert(@event, eventType));
            return Expression.Lambda<Action<object, IDomainEvent>>(call, aggregate, @event).Compile();
        }
    }
}
