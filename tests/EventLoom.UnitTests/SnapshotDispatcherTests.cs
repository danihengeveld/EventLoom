using System.Text.Json;

namespace EventLoom.UnitTests;

public sealed class SnapshotDispatcherTests
{
    [Test]
    public async Task Dispatcher_reads_snapshot_identity_from_the_contract()
    {
        var dispatcher = AggregateSnapshotDispatcher.Create<CounterAggregate, Guid, CounterSnapshot>(null);
        var defaultVersion = AggregateSnapshotDispatcher.Create<DefaultVersionAggregate, Guid, DefaultVersionSnapshot>(null);

        await Assert.That(dispatcher.SnapshotType).IsEqualTo("tests.counter");
        await Assert.That(dispatcher.SchemaVersion).IsEqualTo(2);
        await Assert.That(defaultVersion.SchemaVersion).IsEqualTo(1);
    }

    [Test]
    public async Task Dispatcher_round_trips_aggregate_state_and_stream_version()
    {
        var dispatcher = AggregateSnapshotDispatcher.Create<CounterAggregate, Guid, CounterSnapshot>(null);
        var source = new CounterAggregate(Guid.NewGuid());
        source.Increment(7);

        var payload = dispatcher.Capture(source);
        var restored = new CounterAggregate(source.Id);
        dispatcher.Restore(restored, 2, payload, 12);

        await Assert.That(payload).Contains("\"value\":7");
        await Assert.That(restored.Value).IsEqualTo(7);
        await Assert.That(restored.Version).IsEqualTo(12);
    }

    [Test]
    public async Task Historical_snapshot_is_upcast_before_restore()
    {
        var dispatcher = AggregateSnapshotDispatcher.Create<CounterAggregate, Guid, CounterSnapshot>(
            [new CounterSnapshotV1ToV2()]);
        var aggregate = new CounterAggregate(Guid.NewGuid());

        dispatcher.Restore(aggregate, 1, """{"count":3}""", 4);

        await Assert.That(aggregate.Value).IsEqualTo(3);
    }

    [Test]
    public async Task Incompatible_snapshot_versions_are_rejected()
    {
        var dispatcher = AggregateSnapshotDispatcher.Create<CounterAggregate, Guid, CounterSnapshot>(null);

        await Assert.That(() => dispatcher.Restore(new CounterAggregate(Guid.NewGuid()), 3, "{}", 1))
            .Throws<SnapshotIncompatibleException>();
        await Assert.That(() => dispatcher.Restore(new CounterAggregate(Guid.NewGuid()), 1, "{}", 1))
            .Throws<SnapshotIncompatibleException>();
    }

    [Test]
    public async Task Corrupt_snapshot_payload_is_reported()
    {
        var dispatcher = AggregateSnapshotDispatcher.Create<CounterAggregate, Guid, CounterSnapshot>(null);

        await Assert.That(() => dispatcher.Restore(new CounterAggregate(Guid.NewGuid()), 2, "{", 1))
            .Throws<SnapshotDeserializationException>();
    }

    [Test]
    public async Task Invalid_snapshot_contracts_are_rejected_when_the_dispatcher_is_created()
    {
        await Assert.That(() => AggregateSnapshotDispatcher.Create<EmptyNameAggregate, Guid, EmptyNameSnapshot>(null))
            .Throws<InvalidOperationException>();
        await Assert.That(() => AggregateSnapshotDispatcher.Create<ZeroVersionAggregate, Guid, ZeroVersionSnapshot>(null))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Raise_during_snapshot_capture_or_restore_is_rejected()
    {
        var dispatcher = AggregateSnapshotDispatcher.Create<ReentrantAggregate, Guid, ReentrantSnapshot>(null);
        var aggregate = new ReentrantAggregate(Guid.NewGuid());

        await Assert.That(() => dispatcher.Capture(aggregate)).Throws<NestedRaiseException>();
        await Assert.That(() => dispatcher.Restore(aggregate, 1, "{}", 1)).Throws<NestedRaiseException>();
        await Assert.That(aggregate.Version).IsEqualTo(0);
    }

    private sealed record Incremented(int Amount) : IDomainEvent<Incremented, CounterAggregate>
    {
        public static string EventType => "tests.snapshot-incremented";
    }

    private sealed record CounterSnapshot(int Value) : IAggregateSnapshot<CounterSnapshot, CounterAggregate>
    {
        public static string SnapshotType => "tests.counter";

        public static int SnapshotVersion => 2;
    }

    private sealed record DefaultVersionSnapshot : IAggregateSnapshot<DefaultVersionSnapshot, DefaultVersionAggregate>
    {
        public static string SnapshotType => "tests.default-version";
    }

    private sealed record EmptyNameSnapshot : IAggregateSnapshot<EmptyNameSnapshot, EmptyNameAggregate>
    {
        public static string SnapshotType => "";
    }

    private sealed record ZeroVersionSnapshot : IAggregateSnapshot<ZeroVersionSnapshot, ZeroVersionAggregate>
    {
        public static string SnapshotType => "tests.zero";

        public static int SnapshotVersion => 0;
    }

    private sealed record ReentrantSnapshot : IAggregateSnapshot<ReentrantSnapshot, ReentrantAggregate>
    {
        public static string SnapshotType => "tests.reentrant";
    }

    private sealed record ReentrantHappened : IDomainEvent<ReentrantHappened, ReentrantAggregate>
    {
        public static string EventType => "tests.reentrant-happened";
    }

    private sealed class CounterAggregate(Guid id) : Aggregate<CounterAggregate, Guid>(id),
        IApply<Incremented>,
        ISnapshotable<CounterSnapshot>
    {
        public int Value { get; private set; }

        public void Increment(int amount) => Raise(new Incremented(amount));

        void IApply<Incremented>.Apply(Incremented @event) => Value += @event.Amount;

        CounterSnapshot ISnapshotable<CounterSnapshot>.CreateSnapshot() => new(Value);

        void ISnapshotable<CounterSnapshot>.RestoreSnapshot(CounterSnapshot snapshot) => Value = snapshot.Value;
    }

    private sealed class DefaultVersionAggregate(Guid id) : Aggregate<DefaultVersionAggregate, Guid>(id),
        ISnapshotable<DefaultVersionSnapshot>
    {
        DefaultVersionSnapshot ISnapshotable<DefaultVersionSnapshot>.CreateSnapshot() => new();

        void ISnapshotable<DefaultVersionSnapshot>.RestoreSnapshot(DefaultVersionSnapshot snapshot)
        {
        }
    }

    private sealed class EmptyNameAggregate(Guid id) : Aggregate<EmptyNameAggregate, Guid>(id),
        ISnapshotable<EmptyNameSnapshot>
    {
        EmptyNameSnapshot ISnapshotable<EmptyNameSnapshot>.CreateSnapshot() => new();

        void ISnapshotable<EmptyNameSnapshot>.RestoreSnapshot(EmptyNameSnapshot snapshot)
        {
        }
    }

    private sealed class ZeroVersionAggregate(Guid id) : Aggregate<ZeroVersionAggregate, Guid>(id),
        ISnapshotable<ZeroVersionSnapshot>
    {
        ZeroVersionSnapshot ISnapshotable<ZeroVersionSnapshot>.CreateSnapshot() => new();

        void ISnapshotable<ZeroVersionSnapshot>.RestoreSnapshot(ZeroVersionSnapshot snapshot)
        {
        }
    }

    private sealed class ReentrantAggregate(Guid id) : Aggregate<ReentrantAggregate, Guid>(id),
        IApply<ReentrantHappened>,
        ISnapshotable<ReentrantSnapshot>
    {
        void IApply<ReentrantHappened>.Apply(ReentrantHappened @event)
        {
        }

        ReentrantSnapshot ISnapshotable<ReentrantSnapshot>.CreateSnapshot()
        {
            Raise(new ReentrantHappened());
            return new ReentrantSnapshot();
        }

        void ISnapshotable<ReentrantSnapshot>.RestoreSnapshot(ReentrantSnapshot snapshot) =>
            Raise(new ReentrantHappened());
    }

    private sealed class CounterSnapshotV1ToV2 : ISnapshotUpcaster
    {
        public string SnapshotType => "tests.counter";

        public int FromVersion => 1;

        public int ToVersion => 2;

        public JsonElement Upcast(JsonElement payload) =>
            JsonSerializer.SerializeToElement(new { value = payload.GetProperty("count").GetInt32() });
    }
}
