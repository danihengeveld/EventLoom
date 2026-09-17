using System.Collections.ObjectModel;
using System.Reflection;

namespace EventLoom;

/// <summary>Registers domain events by stable persisted name and schema version.</summary>
public sealed class EventRegistry
{
    private readonly Dictionary<Type, EventRegistration> registrationsByType = [];
    private readonly Dictionary<EventTypeKey, EventRegistration> registrationsByName = [];

    /// <summary>Registers one concrete event type using its stable event metadata.</summary>
    /// <typeparam name="TEvent">The concrete event type to register.</typeparam>
    /// <returns>This registry.</returns>
    public EventRegistry RegisterEvent<TEvent>()
        where TEvent : IDomainEvent
    {
        return RegisterEvent(typeof(TEvent));
    }

    /// <summary>Registers every concrete domain-event type in an assembly.</summary>
    /// <param name="assembly">The assembly containing event types.</param>
    /// <returns>This registry.</returns>
    public EventRegistry RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (var type in assembly.GetTypes()
                     .Where(static type => !type.IsAbstract && typeof(IDomainEvent).IsAssignableFrom(type)))
        {
            RegisterEvent(type);
        }

        return this;
    }

    /// <summary>Gets the registration for a concrete event type.</summary>
    /// <typeparam name="TEvent">The registered event type.</typeparam>
    /// <returns>The event registration.</returns>
    public EventRegistration Get<TEvent>()
        where TEvent : IDomainEvent
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

    /// <summary>Gets the registration for a persisted event name and schema version.</summary>
    /// <param name="eventName">The stable persisted event name.</param>
    /// <param name="version">The positive schema version.</param>
    /// <returns>The event registration.</returns>
    public EventRegistration Get(string eventName, int version)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name is required.", nameof(eventName));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Event schema version must be positive.");
        }

        return registrationsByName.TryGetValue(new EventTypeKey(eventName, version), out var registration)
            ? registration
            : throw new EventNotRegisteredException(eventName, version);
    }

    /// <summary>Gets the latest registered schema version for a persisted event name.</summary>
    /// <param name="eventName">The stable persisted event name.</param>
    /// <returns>The latest event registration.</returns>
    public EventRegistration GetCurrent(string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return registrationsByName.Values
            .Where(registration => registration.Name == eventName)
            .OrderByDescending(registration => registration.Version)
            .FirstOrDefault()
            ?? throw new EventNotRegisteredException(eventName, 0);
    }

    /// <summary>Gets all explicitly registered event types.</summary>
    public IReadOnlyCollection<EventRegistration> Registrations =>
        new ReadOnlyCollection<EventRegistration>(registrationsByType.Values.ToList());

    private EventRegistry RegisterEvent(Type eventType)
    {
        if (!typeof(IDomainEvent).IsAssignableFrom(eventType) || eventType.IsAbstract)
        {
            throw new ArgumentException($"Type '{eventType.FullName}' must be a concrete IDomainEvent.", nameof(eventType));
        }

        var attribute = eventType.GetCustomAttribute<EventTypeAttribute>()
            ?? throw new EventTypeMetadataMissingException(eventType);
        var key = new EventTypeKey(attribute.Name, attribute.Version);

        if (registrationsByType.ContainsKey(eventType))
        {
            throw new DuplicateEventRegistrationException(eventType);
        }

        if (registrationsByName.TryGetValue(key, out var existing))
        {
            throw new DuplicateEventTypeException(attribute.Name, attribute.Version, existing.ClrType, eventType);
        }

        var registration = new EventRegistration(eventType, attribute.Name, attribute.Version);
        registrationsByType.Add(eventType, registration);
        registrationsByName.Add(key, registration);
        return this;
    }

    private EventRegistration GetRegistration(Type eventType) =>
        registrationsByType.TryGetValue(eventType, out var registration)
            ? registration
            : throw new EventNotRegisteredException(eventType);

    private readonly record struct EventTypeKey(string Name, int Version);
}

/// <summary>Describes a registered event type.</summary>
/// <summary>Describes a CLR event type and its stable persisted identity.</summary>
/// <param name="ClrType">The concrete CLR event type.</param>
/// <param name="Name">The stable persisted event name.</param>
/// <param name="Version">The positive event schema version.</param>
public sealed record EventRegistration(Type ClrType, string Name, int Version);

/// <summary>Indicates that a domain-event type has no <see cref="EventTypeAttribute"/>.</summary>
public sealed class EventTypeMetadataMissingException(Type eventType)
    : InvalidOperationException($"Event type '{eventType.FullName}' is missing EventTypeAttribute.");

/// <summary>Indicates that a CLR event type was registered more than once.</summary>
public sealed class DuplicateEventRegistrationException(Type eventType)
    : InvalidOperationException($"Event CLR type '{eventType.FullName}' is registered more than once.");

/// <summary>Indicates that two CLR event types claim the same persisted event identity.</summary>
public sealed class DuplicateEventTypeException(string name, int version, Type existingType, Type duplicateType)
    : InvalidOperationException(
        $"Event type '{name}' version {version} is already registered for '{existingType.FullName}', cannot register '{duplicateType.FullName}'.");

/// <summary>Indicates that a requested CLR or persisted event identity was not registered.</summary>
public sealed class EventNotRegisteredException : InvalidOperationException
{
    public EventNotRegisteredException(Type eventType)
        : base($"Event CLR type '{eventType.FullName}' is not registered.")
    {
    }

    public EventNotRegisteredException(string name, int version)
        : base($"Event type '{name}' version {version} is not registered.")
    {
    }
}
