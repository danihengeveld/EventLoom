using System.Text.Json;
using EventLoom;

namespace EventLoom.UnitTests;

public sealed class SnapshotUpcastingTests
{
    [Test]
    public async Task Adapter_upcasts_a_historical_snapshot_before_restoring()
    {
        SnapshotV2? restored = null;
        var adapter = new AggregateSnapshotAdapter<object, SnapshotV2>(
            _ => new SnapshotV2(0, "EUR"),
            (_, snapshot) => restored = snapshot,
            upcasters: [new SnapshotV1ToV2()]);

        adapter.Restore(new object(), 1, """{"value":5}""");

        await Assert.That(restored).IsEqualTo(new SnapshotV2(5, "EUR"));
    }

    [SnapshotType("tests.counter", Version = 2)]
    private sealed record SnapshotV2(int Value, string Currency) : IAggregateSnapshot;

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
}
