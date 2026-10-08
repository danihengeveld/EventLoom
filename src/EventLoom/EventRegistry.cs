using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace EventLoom;

/// <summary>Registers domain events by stable persisted name and schema version.</summary>
/// <remarks>
/// Events are registered per aggregate: every event an aggregate handles through <see cref="IApply{TEvent}"/>
/// and owns through <see cref="IDomainEvent{TSelf, TAggregate}"/> is registered with its declared identity.
/// Each persisted event name maps to exactly one CLR type, which represents the current schema version;
/// earlier persisted versions are read through upcasters.
/// </remarks>
public sealed class EventRegistry
{
    private static readonly MethodInfo RegisterEventMethod = typeof(EventRegistry)
        .GetMethod(nameof(RegisterEvent), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Dictionary<Type, EventRegistration> registrationsByType = [];
    private readonly Dictionary<string, EventRegistration> registrationsByName = new(StringComparer.Ordinal);
    private readonly HashSet<Type> registeredAggregates = [];

    /// <summary>Registers every event owned and handled by an aggregate.</summary>
    /// <typeparam name="TAggregate">The aggregate whose events are registered.</typeparam>
    /// <returns>This registry.</returns>
    /// <exception cref="InvalidEventContractException">An event declares an empty name or a non-positive version.</exception>
    /// <exception cref="DuplicateEventTypeException">Two CLR event types declare the same persisted name.</exception>
    public EventRegistry RegisterAggregate<TAggregate>()
        where TAggregate : Aggregate
    {
        if (!registeredAggregates.Add(typeof(TAggregate)))
        {
            return this;
        }

        var ownedEvents = typeof(TAggregate).GetInterfaces()
            .Where(static candidate =>
                candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IApply<>))
            .Select(static candidate => candidate.GetGenericArguments()[0])
            .Where(static eventType => typeof(IDomainEvent<TAggregate>).IsAssignableFrom(eventType))
            .OrderBy(static eventType => eventType.FullName, StringComparer.Ordinal);
        foreach (var eventType in ownedEvents)
        {
            try
            {
                RegisterEventMethod.MakeGenericMethod(eventType, typeof(TAggregate)).Invoke(this, null);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(exception.InnerException);
            }
        }

        return this;
    }

    /// <summary>Gets the registration for a concrete event type.</summary>
    /// <typeparam name="TEvent">The registered event type.</typeparam>
    /// <returns>The event registration.</returns>
    public EventRegistration Get<TEvent>()
    {
        return GetRegistration(typeof(TEvent));
    }

    /// <summary>
    /// Gets the registration for a runtime event type.
    /// </summary>
    public EventRegistration Get(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return GetRegistration(eventType);
    }

    /// <summary>Gets the registration, and therefore the current schema version, for a persisted event name.</summary>
    /// <param name="eventName">The stable persisted event name.</param>
    /// <returns>The event registration.</returns>
    public EventRegistration Get(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return registrationsByName.TryGetValue(eventName, out var registration)
            ? registration
            : throw new EventNotRegisteredException(eventName);
    }

    /// <summary>Gets all registered event types.</summary>
    public IReadOnlyCollection<EventRegistration> Registrations =>
        new ReadOnlyCollection<EventRegistration>(registrationsByType.Values.ToList());

    private void RegisterEvent<TEvent, TAggregate>()
        where TEvent : IDomainEvent<TAggregate>
        where TAggregate : Aggregate
    {
        var eventType = typeof(TEvent);
        if (eventType.IsAbstract || eventType.IsInterface)
        {
            throw new InvalidEventContractException(eventType, "event types must be concrete");
        }

        var name = TEvent.PersistedName;
        var version = TEvent.PersistedVersion;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidEventContractException(eventType, "EventType must be a non-empty string");
        }

        if (version <= 0)
        {
            throw new InvalidEventContractException(eventType, "EventVersion must be positive");
        }

        if (registrationsByName.TryGetValue(name, out var existing))
        {
            throw new DuplicateEventTypeException(name, existing.ClrType, eventType);
        }

        var registration = new EventRegistration(eventType, name, version, typeof(TAggregate));
        registrationsByType.Add(eventType, registration);
        registrationsByName.Add(name, registration);
    }

    private EventRegistration GetRegistration(Type eventType) =>
        registrationsByType.TryGetValue(eventType, out var registration)
            ? registration
            : throw new EventNotRegisteredException(eventType);
}

/// <summary>Describes a CLR event type and its stable persisted identity.</summary>
/// <param name="ClrType">The concrete CLR event type.</param>
/// <param name="Name">The stable persisted event name.</param>
/// <param name="Version">The current positive event schema version.</param>
/// <param name="AggregateType">The aggregate that owns and applies the event.</param>
public sealed record EventRegistration(Type ClrType, string Name, int Version, Type AggregateType);

/// <summary>Indicates that an event declares an invalid persisted identity.</summary>
public sealed class InvalidEventContractException(Type eventType, string reason)
    : InvalidOperationException($"Event type '{eventType.FullName}' is invalid: {reason}.");

/// <summary>Indicates that two CLR event types claim the same persisted event name.</summary>
public sealed class DuplicateEventTypeException(string name, Type existingType, Type duplicateType)
    : InvalidOperationException(
        $"Event type '{name}' is already registered for '{existingType.FullName}', cannot register '{duplicateType.FullName}'. " +
        "Each persisted event name has exactly one CLR type; model schema changes with EventVersion and upcasters.");

/// <summary>Indicates that a requested CLR or persisted event identity was not registered.</summary>
public sealed class EventNotRegisteredException : InvalidOperationException
{
    /// <summary>Initializes an exception for an unregistered CLR event type.</summary>
    public EventNotRegisteredException(Type eventType)
        : base($"Event CLR type '{eventType.FullName}' is not registered. Register its aggregate.")
    {
    }

    /// <summary>Initializes an exception for an unregistered persisted event name.</summary>
    public EventNotRegisteredException(string name)
        : base($"Event type '{name}' is not registered.")
    {
    }

    /// <summary>Initializes an exception for a persisted event version newer than the registered version.</summary>
    public EventNotRegisteredException(string name, int version)
        : base($"Event type '{name}' version {version} is newer than the registered event version.")
    {
    }
}
