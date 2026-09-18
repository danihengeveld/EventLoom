using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EventLoom;

/// <summary>Serializes registered domain events and deserializes their persisted payloads.</summary>
public sealed class EventSerializer
{
    private readonly EventRegistry registry;
    private readonly JsonSerializerOptions options;
    private readonly IReadOnlyDictionary<string, EventUpcasterChain> upcasterChains;

    /// <summary>
    /// Initializes an event serializer.
    /// </summary>
    /// <param name="registry">The registry used to resolve stable event identities.</param>
    /// <param name="contexts">Optional source-generated JSON contexts.</param>
    /// <param name="reflectionFallback">Whether reflection metadata is available when no context is supplied.</param>
    /// <param name="upcasterChains">Optional deterministic upcaster chains keyed by event name.</param>
    public EventSerializer(
        EventRegistry registry,
        IEnumerable<JsonSerializerContext>? contexts = null,
        bool reflectionFallback = true,
        IEnumerable<EventUpcasterChain>? upcasterChains = null)
        : this(
            registry,
            CreateOptions(contexts, reflectionFallback),
            upcasterChains)
    {
    }

    /// <summary>
    /// Initializes an event serializer with cohesive JSON serialization settings.
    /// </summary>
    /// <param name="registry">The registry used to resolve stable event identities.</param>
    /// <param name="serializationOptions">The JSON serialization settings.</param>
    /// <param name="upcasterChains">Optional deterministic upcaster chains keyed by event name.</param>
    public EventSerializer(
        EventRegistry registry,
        EventSerializationOptions serializationOptions,
        IEnumerable<EventUpcasterChain>? upcasterChains)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(serializationOptions);
        this.upcasterChains = (upcasterChains ?? [])
            .ToDictionary(chain => chain.EventName, StringComparer.Ordinal);
        options = serializationOptions.CreateSerializerOptions();
    }

    /// <summary>Initializes an event serializer with cohesive JSON serialization settings.</summary>
    public EventSerializer(EventRegistry registry, EventSerializationOptions serializationOptions)
        : this(registry, serializationOptions, null)
    {
    }

    /// <summary>Serializes a registered event to its JSON payload.</summary>
    /// <typeparam name="TEvent">The registered event type.</typeparam>
    /// <param name="event">The event to serialize.</param>
    /// <returns>The serialized JSON payload.</returns>
    public string Serialize<TEvent>(TEvent @event)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        var registration = registry.Get<TEvent>();
        return JsonSerializer.Serialize(@event, registration.ClrType, options);
    }

    /// <summary>Deserializes a stored event, applying upcasters before materializing the current CLR type.</summary>
    /// <param name="eventName">The stable persisted event name.</param>
    /// <param name="version">The persisted event schema version.</param>
    /// <param name="payload">The stored JSON payload.</param>
    /// <returns>The current registered domain-event instance.</returns>
    public IDomainEvent Deserialize(string eventName, int version, string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        _ = registry.Get(eventName, version);
        var currentRegistration = registry.GetCurrent(eventName);
        var normalizedPayload = payload;
        if (version < currentRegistration.Version)
        {
            if (!upcasterChains.TryGetValue(eventName, out var chain))
            {
                throw new EventUpcastChainException(
                    eventName,
                    $"No upcaster chain is registered from version {version} to {currentRegistration.Version}.");
            }

            using var document = JsonDocument.Parse(payload);
            normalizedPayload = chain.Upcast(document.RootElement, version, currentRegistration.Version).GetRawText();
        }

        try
        {
            return (IDomainEvent)(JsonSerializer.Deserialize(
                    normalizedPayload,
                    currentRegistration.ClrType,
                    options)
                ?? throw new EventDeserializationException(eventName, version));
        }
        catch (JsonException exception)
        {
            throw new EventDeserializationException(eventName, version, exception);
        }
    }

    /// <summary>Serializes a registered event and returns its stable persisted identity and payload.</summary>
    /// <typeparam name="TEvent">The registered event type.</typeparam>
    /// <param name="event">The event to serialize.</param>
    /// <returns>The persisted payload description.</returns>
    public SerializedEventPayload SerializePayload<TEvent>(TEvent @event)
        where TEvent : IDomainEvent
    {
        var registration = registry.Get<TEvent>();
        return new SerializedEventPayload(
            registration.Name,
            registration.Version,
            Serialize(@event));
    }

    /// <summary>
    /// Serializes a domain event using its runtime registered type.
    /// </summary>
    public SerializedEventPayload SerializePayload(IDomainEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var registration = registry.Get(@event.GetType());
        return new SerializedEventPayload(
            registration.Name,
            registration.Version,
            JsonSerializer.Serialize(@event, registration.ClrType, options));
    }

    /// <summary>
    /// Validates that every registered event has usable serialization metadata and supports a null JSON round-trip.
    /// </summary>
    /// <exception cref="EventSerializationValidationException">A registered event cannot be handled by the configured serializer.</exception>
    public void ValidateRegisteredEvents()
    {
        foreach (var registration in registry.Registrations.OrderBy(value => value.Name, StringComparer.Ordinal)
                     .ThenBy(value => value.Version))
        {
            try
            {
                var payload = JsonSerializer.Serialize((object?)null, registration.ClrType, options);
                _ = JsonSerializer.Deserialize(payload, registration.ClrType, options);
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
            {
                throw new EventSerializationValidationException(registration, exception);
            }
        }
    }

    /// <summary>Describes the JSON payload and stable identity written for an event.</summary>
    /// <param name="EventName">The stable persisted event name.</param>
    /// <param name="Version">The persisted event schema version.</param>
    /// <param name="Payload">The JSON event payload.</param>
    public sealed record SerializedEventPayload(string EventName, int Version, string Payload);

    private static EventSerializationOptions CreateOptions(
        IEnumerable<JsonSerializerContext>? contexts,
        bool reflectionFallback)
    {
        var serializationOptions = new EventSerializationOptions
        {
            ReflectionFallback = reflectionFallback
        };
        if (contexts is not null)
        {
            foreach (var context in contexts)
            {
                serializationOptions.Contexts.Add(context);
            }
        }

        return serializationOptions;
    }
}

/// <summary>Indicates that a stored event payload could not be materialized as its registered event type.</summary>
public sealed class EventDeserializationException : InvalidOperationException
{
    public EventDeserializationException(string eventName, int version, Exception? innerException = null)
        : base($"Could not deserialize registered event '{eventName}' version {version}.", innerException)
    {
    }
}

/// <summary>Indicates that a registered event is incompatible with the configured JSON serializer.</summary>
public sealed class EventSerializationValidationException : InvalidOperationException
{
    public EventSerializationValidationException(EventRegistration registration, Exception innerException)
        : base(
            $"Event '{registration.Name}' version {registration.Version} ({registration.ClrType.FullName}) " +
            "cannot be serialized and deserialized with the configured EventSerializationOptions. " +
            "Register its source-generated JsonSerializerContext or enable reflection fallback.",
            innerException)
    {
    }
}
