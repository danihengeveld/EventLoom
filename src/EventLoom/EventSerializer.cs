using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EventLoom;

public sealed class EventSerializer
{
    private readonly EventRegistry registry;
    private readonly JsonSerializerOptions options;
    private readonly IReadOnlyDictionary<string, EventUpcasterChain> upcasterChains;

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

    public string Serialize<TEvent>(TEvent @event)
        where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        var registration = registry.Get<TEvent>();
        return JsonSerializer.Serialize(@event, registration.ClrType, options);
    }

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

    public sealed record SerializedEventPayload(string EventName, int Version, string Payload);

    private sealed class EmptyJsonTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }
}

public sealed class EventDeserializationException : InvalidOperationException
{
    public EventDeserializationException(string eventName, int version, Exception? innerException = null)
        : base($"Could not deserialize registered event '{eventName}' version {version}.", innerException)
    {
    }
}
