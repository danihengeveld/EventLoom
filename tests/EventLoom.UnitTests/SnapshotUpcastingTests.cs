using System.Text.Json;

namespace EventLoom.UnitTests;

public sealed class SnapshotUpcastingTests
{
    [Test]
    public async Task Snapshot_upcaster_chain_transforms_a_historical_snapshot()
    {
        var chain = new SnapshotUpcasterChain("tests.counter", [new SnapshotV1ToV2()]);
        var payload = chain.Upcast(JsonSerializer.SerializeToElement(new { value = 5 }), 1, 2);

        var snapshot = JsonSerializer.Deserialize<SnapshotV2>(
            payload.GetRawText(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        await Assert.That(snapshot).IsEqualTo(new SnapshotV2(5, "EUR"));
    }

    [Test]
    public async Task Invalid_upcaster_edges_are_rejected_at_construction()
    {
        await Assert.That(() => new SnapshotUpcasterChain(
                "tests.counter",
                [new InvalidSnapshotUpcaster()]))
            .Throws<SnapshotUpcastException>();
    }

    [Test]
    public async Task Upcaster_failures_are_reported_as_safe_snapshot_errors()
    {
        var chain = new SnapshotUpcasterChain("tests.counter", [new ThrowingSnapshotUpcaster()]);

        await Assert.That(() => chain.Upcast(JsonSerializer.SerializeToElement(new { }), 1, 2))
            .Throws<SnapshotUpcastException>();
    }

    [SnapshotType("tests.counter", Version = 2)]
    private sealed record SnapshotV2(int Value, string Currency) : IAggregateSnapshot<SnapshotAggregate>;

    private sealed class SnapshotAggregate(Guid id) : Aggregate<Guid>(id);

    private sealed class SnapshotV1ToV2 : ISnapshotUpcaster
    {
        public string SnapshotType => "tests.counter";
        public int FromVersion => 1;
        public int ToVersion => 2;

        public JsonElement Upcast(JsonElement payload)
        {
            using var document = JsonDocument.Parse(
                $$"""{"value":{{payload.GetProperty("value").GetRawText()}},"currency":"EUR"}""");
            return document.RootElement.Clone();
        }
    }

    private sealed class InvalidSnapshotUpcaster : ISnapshotUpcaster
    {
        public string SnapshotType => "tests.counter";
        public int FromVersion => 1;
        public int ToVersion => 3;
        public JsonElement Upcast(JsonElement payload) => payload;
    }

    private sealed class ThrowingSnapshotUpcaster : ISnapshotUpcaster
    {
        public string SnapshotType => "tests.counter";
        public int FromVersion => 1;
        public int ToVersion => 2;

        public JsonElement Upcast(JsonElement payload) => throw new InvalidOperationException();
    }
}
