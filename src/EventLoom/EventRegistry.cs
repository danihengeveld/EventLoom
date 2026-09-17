using System.Collections.ObjectModel;
using System.Reflection;

namespace EventLoom;

public sealed class EventRegistry
{
    private readonly Dictionary<Type, EventRegistration> registrationsByType = [];
    private readonly Dictionary<EventTypeKey, EventRegistration> registrationsByName = [];

    public EventRegistry RegisterEvent<TEvent>()
        where TEvent : IDomainEvent
    {
        return RegisterEvent(typeof(TEvent));
    }

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

    public EventRegistration Get<TEvent>()
        where TEvent : IDomainEvent
    {
        return Get(typeof(TEvent));
    }

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

    private EventRegistration Get(Type eventType) =>
        registrationsByType.TryGetValue(eventType, out var registration)
            ? registration
            : throw new EventNotRegisteredException(eventType);

    private readonly record struct EventTypeKey(string Name, int Version);
}

public sealed record EventRegistration(Type ClrType, string Name, int Version);

public sealed class EventTypeMetadataMissingException(Type eventType)
    : InvalidOperationException($"Event type '{eventType.FullName}' is missing EventTypeAttribute.");

public sealed class DuplicateEventRegistrationException(Type eventType)
    : InvalidOperationException($"Event CLR type '{eventType.FullName}' is registered more than once.");

public sealed class DuplicateEventTypeException(string name, int version, Type existingType, Type duplicateType)
    : InvalidOperationException(
        $"Event type '{name}' version {version} is already registered for '{existingType.FullName}', cannot register '{duplicateType.FullName}'.");

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
