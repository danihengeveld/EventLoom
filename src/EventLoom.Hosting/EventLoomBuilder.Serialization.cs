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

}
