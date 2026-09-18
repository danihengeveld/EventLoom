using System.Text.Json;

namespace EventLoom.UnitTests;

public sealed class EventUpcastingTests
{
    [Test]
    public async Task Upcasters_are_applied_in_order()
    {
        var chain = new EventUpcasterChain(
            "tests.versioned",
            [new AddPropertyUpcaster(1, "middle", 2), new AddPropertyUpcaster(2, "final", 3)]);

        var result = chain.Upcast(JsonDocument.Parse("""{"start":true}""").RootElement, 1, 3);

        await Assert.That(result.GetProperty("middle").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("final").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Missing_version_gap_is_rejected()
    {
        var chain = new EventUpcasterChain("tests.versioned", [new AddPropertyUpcaster(2, "final", 3)]);

        await Assert.That(() => chain.Upcast(JsonDocument.Parse("{}").RootElement, 1, 3))
            .Throws<EventUpcastChainException>();
    }

    [Test]
    public async Task Duplicate_edges_are_rejected_at_startup()
    {
        await Assert.That(() => new EventUpcasterChain(
                "tests.versioned",
                [new AddPropertyUpcaster(1, "one", 2), new AddPropertyUpcaster(1, "other", 2)]))
            .Throws<EventUpcastChainException>();
    }

    private sealed class AddPropertyUpcaster(int fromVersion, string property, int toVersion) : IEventUpcaster
    {
        public string EventName => "tests.versioned";

        public int FromVersion { get; } = fromVersion;

        public int ToVersion { get; } = toVersion;

        public JsonElement Upcast(JsonElement payload)
        {
            var values = payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            values[property] = JsonDocument.Parse("true").RootElement;
            return JsonSerializer.SerializeToElement(values);
        }
    }
}
