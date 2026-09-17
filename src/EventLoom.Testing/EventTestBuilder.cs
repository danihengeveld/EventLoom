using System.Text.Json;

namespace EventLoom.Testing;

public sealed class EventTestBuilder<TEvent>
    where TEvent : EventLoom.IDomainEvent
{
    private TEvent? value;

    public EventTestBuilder<TEvent> With(TEvent @event)
    {
        value = @event;
        return this;
    }

    public TEvent Build() =>
        value ?? throw new InvalidOperationException($"No {typeof(TEvent).Name} was configured.");

    public string Serialize(EventLoom.EventSerializer serializer) =>
        serializer.Serialize(Build());

    public JsonDocument SerializeToDocument(EventLoom.EventSerializer serializer) =>
        JsonDocument.Parse(Serialize(serializer));
}
