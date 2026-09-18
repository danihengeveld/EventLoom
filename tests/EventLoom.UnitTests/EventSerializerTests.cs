using System.Text.Json;

namespace EventLoom.UnitTests;

public sealed partial class EventSerializerTests
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
    public async Task Historical_payload_is_upcast_to_current_event_type()
    {
        var registry = new EventRegistry()
            .RegisterEvent<VersionOneEvent>()
            .RegisterEvent<VersionTwoEvent>();
        var chain = new EventUpcasterChain("tests.evolving", [new AddQuantityUpcaster()]);
        var serializer = new EventSerializer(registry, upcasterChains: [chain]);

        var result = serializer.Deserialize("tests.evolving", 1, """{"name":"abc"}""");

        await Assert.That(result).IsEqualTo(new VersionTwoEvent("abc", 1));
    }

    [Test]
    public async Task Serialize_payload_contains_stable_name_and_version()
    {
        var registry = new EventRegistry().RegisterEvent<SerializedEvent>();
        var serializer = new EventSerializer(registry);

        var result = serializer.SerializePayload(new SerializedEvent("abc", 4));

        await Assert.That(result.EventName).IsEqualTo("tests.serialized");
        await Assert.That(result.Version).IsEqualTo(1);
        await Assert.That(result.Payload).Contains("\"name\":\"abc\"");
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

    [EventType("tests.serialized")]
    private sealed record SerializedEvent(string Name, int Quantity) : IDomainEvent<TestAggregate>;

    [EventType("tests.evolving", Version = 1)]
    private sealed record VersionOneEvent(string Name) : IDomainEvent<TestAggregate>;

    [EventType("tests.evolving", Version = 2)]
    private sealed record VersionTwoEvent(string Name, int Quantity) : IDomainEvent<TestAggregate>;

    private sealed class AddQuantityUpcaster : IEventUpcaster
    {
        public string EventName => "tests.evolving";

        public int FromVersion => 1;

        public int ToVersion => 2;

        public JsonElement Upcast(JsonElement payload) =>
            JsonSerializer.SerializeToElement(new { name = payload.GetProperty("name").GetString(), quantity = 1 });
    }

    public sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(SerializedEvent @event)
        {
        }

        private void Apply(VersionOneEvent @event)
        {
        }

        private void Apply(VersionTwoEvent @event)
        {
        }

    }
}
