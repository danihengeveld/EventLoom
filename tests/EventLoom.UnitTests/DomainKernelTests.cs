namespace EventLoom.UnitTests;

public sealed class DomainKernelTests
{
    [Test]
    public async Task Raise_applies_event_and_tracks_pending_event()
    {
        var aggregate = new CounterAggregate(Guid.NewGuid());

        aggregate.Increment(3);

        await Assert.That(aggregate.Value).IsEqualTo(3);
        await Assert.That(aggregate.Version).IsEqualTo(1);
        await Assert.That(aggregate.PendingEvents.Count).IsEqualTo(1);
        await Assert.That(aggregate.PendingEvents[0].StreamVersion).IsEqualTo(1);
    }

    [Test]
    public async Task Replay_applies_history_without_pending_events()
    {
        var aggregate = new CounterAggregate(Guid.NewGuid());

        aggregate.ReplayHistory([new Incremented(2), new Incremented(4)]);

        await Assert.That(aggregate.Value).IsEqualTo(6);
        await Assert.That(aggregate.Version).IsEqualTo(2);
        await Assert.That(aggregate.PendingEvents).IsEmpty();
    }

    [Test]
    public async Task Expected_version_semantics_are_explicit()
    {
        await Assert.That(ExpectedVersion.Exact(3).IsMatch(3)).IsTrue();
        await Assert.That(ExpectedVersion.NoStream.IsMatch(null)).IsTrue();
        await Assert.That(ExpectedVersion.StreamExists.IsMatch(0)).IsTrue();
        await Assert.That(ExpectedVersion.Any.IsMatch(null)).IsTrue();
        await Assert.That(ExpectedVersion.Exact(3)).IsNotEqualTo(ExpectedVersion.Exact(4));
    }

    [Test]
    public async Task Tenant_ids_are_normalized()
    {
        var tenant = new TenantId("  Acme  ");

        await Assert.That(tenant.Value).IsEqualTo("acme");
        await Assert.That(() => new TenantId(" \t ")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Event_metadata_copies_headers()
    {
        var headers = new Dictionary<string, string> { ["key"] = "value" };
        var metadata = new EventMetadata(Headers: headers);
        headers["key"] = "changed";

        await Assert.That(metadata.Headers["key"]).IsEqualTo("value");
    }

    [Test]
    public async Task Pending_events_are_read_only()
    {
        var aggregate = new CounterAggregate(Guid.NewGuid());
        aggregate.Increment(1);

        await Assert.That(() => ((IList<Aggregate<CounterAggregate, Guid>.PendingEvent>)aggregate.PendingEvents).Clear())
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task Generic_base_aggregate_can_share_state_with_a_concrete_owner()
    {
        var aggregate = new DerivedCounterAggregate(Guid.NewGuid());

        aggregate.Increment(5);

        await Assert.That(aggregate.Value).IsEqualTo(5);
        await Assert.That(aggregate.Version).IsEqualTo(1);
    }

    [Test]
    public async Task Struct_events_are_raised_and_replayed()
    {
        var aggregate = new CounterAggregate(Guid.NewGuid());

        aggregate.Reset();
        aggregate.ReplayHistory([new Reset()]);

        await Assert.That(aggregate.Version).IsEqualTo(2);
        await Assert.That(aggregate.PendingEvents[0].Event).IsEqualTo(new Reset());
    }

    [Test]
    public async Task Replay_rejects_an_event_owned_by_another_aggregate()
    {
        var aggregate = new OtherAggregate(Guid.NewGuid());

        await Assert.That(() => aggregate.ReplayHistory([new Incremented(1)]))
            .Throws<EventOwnershipException>();
        await Assert.That(aggregate.Version).IsEqualTo(0);
    }

    [Test]
    public async Task Cached_event_ownership_preserves_replay_and_rejection()
    {
        var aggregate = new CounterAggregate(Guid.NewGuid());
        aggregate.ReplayHistory(Enumerable.Repeat<object>(new Incremented(1), 100));

        await Assert.That(aggregate.Value).IsEqualTo(100);
        await Assert.That(aggregate.Version).IsEqualTo(100);
        await Assert.That(() => aggregate.ReplayHistory([new OtherHappened()]))
            .Throws<EventOwnershipException>();
        await Assert.That(() => aggregate.ReplayHistory(["not an event"]))
            .Throws<EventOwnershipException>();
    }

    [Test]
    public async Task Aggregate_must_pass_itself_as_self_type()
    {
        await Assert.That(() => new MisdeclaredAggregate(Guid.NewGuid()))
            .Throws<InvalidAggregateTypeException>();
    }

    [Test]
    public async Task Raise_from_an_apply_handler_is_rejected_without_changing_state()
    {
        var aggregate = new ReentrantAggregate(Guid.NewGuid());

        await Assert.That(() => aggregate.Start()).Throws<NestedRaiseException>();
        await Assert.That(aggregate.Version).IsEqualTo(0);
        await Assert.That(aggregate.PendingEvents).IsEmpty();

        aggregate.Finish();
        await Assert.That(aggregate.Version).IsEqualTo(1);
    }

    [Test]
    public async Task Aggregate_reports_whether_it_is_pristine()
    {
        var aggregate = new CounterAggregate(Guid.NewGuid());
        await Assert.That(aggregate.IsPristine).IsTrue();

        aggregate.Increment(1);
        await Assert.That(aggregate.IsPristine).IsFalse();
    }

    [Test]
    public async Task Time_provider_and_event_id_generator_are_deterministic()
    {
        var now = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
        var clock = new FrozenTimeProvider(now);
        var id = Guid.Parse("0198f2c3-2c00-7000-8000-000000000001");
        var generator = new FixedEventIdGenerator(id);

        await Assert.That(clock.GetUtcNow()).IsEqualTo(now);
        await Assert.That(generator.Create()).IsEqualTo(id);
    }

    private sealed record Incremented(int Amount) : IDomainEvent<Incremented, CounterAggregate>
    {
        public static string EventType => "counter.incremented";
    }

    private readonly record struct Reset : IDomainEvent<Reset, CounterAggregate>
    {
        public static string EventType => "counter.reset";
    }

    private sealed record DerivedIncremented(int Amount) : IDomainEvent<DerivedIncremented, DerivedCounterAggregate>
    {
        public static string EventType => "counter.derived-incremented";
    }

    private sealed record OtherHappened : IDomainEvent<OtherHappened, OtherAggregate>
    {
        public static string EventType => "counter.other-happened";
    }

    private sealed record Started : IDomainEvent<Started, ReentrantAggregate>
    {
        public static string EventType => "counter.started";
    }

    private sealed record Finished : IDomainEvent<Finished, ReentrantAggregate>
    {
        public static string EventType => "counter.finished";
    }

    private sealed class CounterAggregate(Guid id) : Aggregate<CounterAggregate, Guid>(id),
        IApply<Incremented>,
        IApply<Reset>
    {
        public int Value { get; private set; }

        public void Increment(int amount) => Raise(new Incremented(amount));

        public void Reset() => Raise(new Reset());

        public void ReplayHistory(IEnumerable<object> history) => Replay(history);

        void IApply<Incremented>.Apply(Incremented @event) => Value += @event.Amount;

        void IApply<Reset>.Apply(Reset @event) => Value = 0;
    }

    private abstract class BaseCounterAggregate<TSelf>(Guid id) : Aggregate<TSelf, Guid>(id)
        where TSelf : BaseCounterAggregate<TSelf>
    {
        public int Value { get; protected set; }
    }

    private sealed class DerivedCounterAggregate(Guid id) : BaseCounterAggregate<DerivedCounterAggregate>(id),
        IApply<DerivedIncremented>
    {
        public void Increment(int amount) => Raise(new DerivedIncremented(amount));

        void IApply<DerivedIncremented>.Apply(DerivedIncremented @event) => Value += @event.Amount;
    }

    private sealed class OtherAggregate(Guid id) : Aggregate<OtherAggregate, Guid>(id), IApply<OtherHappened>
    {
        public void ReplayHistory(IEnumerable<object> history) => Replay(history);

        void IApply<OtherHappened>.Apply(OtherHappened @event)
        {
        }
    }

    private sealed class MisdeclaredAggregate(Guid id) : Aggregate<CounterAggregate, Guid>(id);

    private sealed class ReentrantAggregate(Guid id) : Aggregate<ReentrantAggregate, Guid>(id),
        IApply<Started>,
        IApply<Finished>
    {
        public void Start() => Raise(new Started());

        public void Finish() => Raise(new Finished());

        void IApply<Started>.Apply(Started @event) => Raise(new Finished());

        void IApply<Finished>.Apply(Finished @event)
        {
        }
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedEventIdGenerator(Guid id) : IEventIdGenerator
    {
        public Guid Create() => id;
    }
}
