using System.Text.Json;
using System.Text.Json.Serialization;
using EventLoom;

namespace EventLoom.UnitTests;

public sealed class EventSerializerTests
{
    [Test]
    public async Task Reflection_serializer_round_trips_registered_event()
    {
        var registry = new EventRegistry().RegisterEvent<SerializedEvent>();
        var serializer = new EventSerializer(registry);

        var payload = serializer.Serialize(new SerializedEvent("abc", 4));
        var result = serializer.Deserialize("tests.serialized", 1, payload);

        await Assert.That(result).IsEqualTo(new SerializedEvent("abc", 4));
    }

    [Test]
    public async Task Corrupt_payload_reports_safe_event_identity()
    {
        var registry = new EventRegistry().RegisterEvent<SerializedEvent>();
        var serializer = new EventSerializer(registry);

        var exception = await Assert.That(() => serializer.Deserialize("tests.serialized", 1, "{"))
            .Throws<EventDeserializationException>();

        await Assert.That(exception!.Message).Contains("tests.serialized");
        await Assert.That(exception.Message).DoesNotContain("{");
    }

    [Test]
    public async Task Strict_mode_rejects_events_without_source_generated_metadata()
    {
        var registry = new EventRegistry().RegisterEvent<SerializedEvent>();
        var serializer = new EventSerializer(registry, reflectionFallback: false);

        await Assert.That(() => serializer.Serialize(new SerializedEvent("abc", 4)))
            .Throws<NotSupportedException>();
    }

    [EventType("tests.serialized")]
    private sealed record SerializedEvent(string Name, int Quantity) : IDomainEvent;
}
