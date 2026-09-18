using System.Text.Json.Serialization;

namespace EventLoom.Hosting;

public sealed partial class EventLoomBuilder
{
    private readonly EventSerializationOptions serializationOptions = new();

    private EventSerializationOptions SerializationOptions => serializationOptions;

    /// <summary>Configures JSON serialization for registered domain events.</summary>
    /// <param name="configure">Configures resolver composition, naming, converters, and safety policies.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    public EventLoomBuilder ConfigureEventSerialization(Action<EventSerializationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(serializationOptions);
        return this;
    }

    /// <summary>Adds a source-generated JSON context to the event serializer.</summary>
    /// <param name="context">The source-generated context.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder AddJsonSerializerContext(JsonSerializerContext context)
    {
        serializationOptions.Contexts.Add(context ?? throw new ArgumentNullException(nameof(context)));
        return this;
    }
}
