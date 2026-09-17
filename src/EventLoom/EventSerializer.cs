using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EventLoom;

public sealed class EventSerializer
{
    private readonly EventRegistry registry;
    private readonly JsonSerializerOptions options;

    public EventSerializer(
        EventRegistry registry,
        IEnumerable<JsonSerializerContext>? contexts = null,
        bool reflectionFallback = true)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
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
        var registration = registry.Get(eventName, version);

        try
        {
            return (IDomainEvent)(JsonSerializer.Deserialize(payload, registration.ClrType, options)
                ?? throw new EventDeserializationException(eventName, version));
        }
        catch (JsonException exception)
        {
            throw new EventDeserializationException(eventName, version, exception);
        }
    }

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
