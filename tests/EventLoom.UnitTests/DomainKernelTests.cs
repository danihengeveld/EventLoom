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
    public async Task Missing_handler_is_reported()
    {
        var aggregate = new MissingHandlerAggregate(Guid.NewGuid());

        await Assert.That(() => aggregate.RaiseUnknown(new MissingHandlerEvent()))
            .Throws<MissingApplyHandlerException>();
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

        await Assert.That(() => ((IList<Aggregate<Guid>.PendingEvent>)aggregate.PendingEvents).Clear())
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task Apply_handler_can_be_declared_on_a_base_aggregate()
    {
        var aggregate = new DerivedCounterAggregate(Guid.NewGuid());

        aggregate.Increment(5);

        await Assert.That(aggregate.Value).IsEqualTo(5);
    }

    [Test]
    public async Task Static_apply_handlers_are_rejected()
    {
        var aggregate = new StaticHandlerAggregate(Guid.NewGuid());

        await Assert.That(() => aggregate.RaiseUnknown(new StaticHandlerEvent()))
            .Throws<InvalidApplyHandlerException>();
    }

    [Test]
    public async Task Aggregate_cannot_raise_an_event_owned_by_another_aggregate()
    {
        var aggregate = new OtherAggregate(Guid.NewGuid());

        await Assert.That(() => aggregate.RaiseForeign(new Incremented(1)))
            .Throws<EventOwnershipException>();
    }

    [Test]
    public async Task Cached_event_ownership_preserves_replay_and_rejection()
    {
        var aggregate = new CounterAggregate(Guid.NewGuid());
        aggregate.ReplayHistory(Enumerable.Repeat<object>(new Incremented(1), 100));

        await Assert.That(aggregate.Value).IsEqualTo(100);
        await Assert.That(aggregate.Version).IsEqualTo(100);
        await Assert.That(() => aggregate.ReplayHistory([new MissingHandlerEvent()]))
            .Throws<EventOwnershipException>();
    }

    [Test]
    public async Task Event_type_requires_a_positive_version()
    {
        await Assert.That(() => new EventTypeAttribute("invalid") { Version = 0 })
            .Throws<ArgumentOutOfRangeException>();
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

    [EventType("counter.incremented")]
    private sealed record Incremented(int Amount) : IDomainEvent<CounterAggregate>;

    [EventType("counter.derived-incremented")]
    private sealed record DerivedIncremented(int Amount) : IDomainEvent<BaseCounterAggregate>;

    [EventType("counter.missing-handler")]
    private sealed record MissingHandlerEvent : IDomainEvent<MissingHandlerAggregate>;

    [EventType("counter.static-handler")]
    private sealed record StaticHandlerEvent : IDomainEvent<StaticHandlerAggregate>;

    private sealed class CounterAggregate(Guid id) : Aggregate<Guid>(id)
    {
        public int Value { get; private set; }

        public void Increment(int amount) => Raise(new Incremented(amount));

        public void ReplayHistory(IEnumerable<object> history) => Replay(history);

        private void Apply(Incremented @event) => Value += @event.Amount;
    }

    private sealed class MissingHandlerAggregate(Guid id) : Aggregate<Guid>(id)
    {
        public void RaiseUnknown(MissingHandlerEvent @event) => Raise(@event);
    }

    private abstract class BaseCounterAggregate(Guid id) : Aggregate<Guid>(id)
    {
        public int Value { get; private set; }

        protected void Apply(DerivedIncremented @event) => Value += @event.Amount;
    }

    private sealed class DerivedCounterAggregate(Guid id) : BaseCounterAggregate(id)
    {
        public void Increment(int amount) => Raise(new DerivedIncremented(amount));
    }

    private sealed class StaticHandlerAggregate(Guid id) : Aggregate<Guid>(id)
    {
        public void RaiseUnknown(StaticHandlerEvent @event) => Raise(@event);

        private static void Apply(StaticHandlerEvent @event)
        {
        }
    }

    private sealed class OtherAggregate(Guid id) : Aggregate<Guid>(id)
    {
        public void RaiseForeign(Incremented @event) => Raise(@event);
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
