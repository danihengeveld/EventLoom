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
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.upcasterChains = (upcasterChains ?? [])
            .ToDictionary(chain => chain.EventName, StringComparer.Ordinal);
        var contextList = contexts?.ToArray() ?? [];

        options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false
        };

        if (contextList.Length > 0)
        {
            options.TypeInfoResolver = JsonTypeInfoResolver.Combine(contextList);
        }
        else if (!reflectionFallback)
        {
            options.TypeInfoResolver = new EmptyJsonTypeInfoResolver();
        }
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

    /// <summary>Describes the JSON payload and stable identity written for an event.</summary>
    /// <param name="EventName">The stable persisted event name.</param>
    /// <param name="Version">The persisted event schema version.</param>
    /// <param name="Payload">The JSON event payload.</param>
    public sealed record SerializedEventPayload(string EventName, int Version, string Payload);

    private sealed class EmptyJsonTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
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
