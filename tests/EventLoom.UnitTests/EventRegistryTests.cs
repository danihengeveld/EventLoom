namespace EventLoom.UnitTests;

public sealed class EventRegistryTests
{
    [Test]
    public async Task Register_aggregate_exposes_stable_metadata_for_owned_events()
    {
        var registry = new EventRegistry().RegisterAggregate<TestAggregate>();

        var registration = registry.Get("tests.registered");

        await Assert.That(registration).IsEqualTo(
            new EventRegistration(typeof(RegisteredEvent), "tests.registered", 2, typeof(TestAggregate)));
        await Assert.That(registry.Get<RegisteredEvent>()).IsEqualTo(registration);
        await Assert.That(registry.Get<DefaultVersionEvent>().Version).IsEqualTo(1);
        await Assert.That(registry.Registrations.Select(value => value.ClrType))
            .IsEquivalentTo([typeof(RegisteredEvent), typeof(DefaultVersionEvent), typeof(StructEvent)]);
    }

    [Test]
    public async Task Register_aggregate_ignores_handlers_for_events_owned_by_another_aggregate()
    {
        var registry = new EventRegistry().RegisterAggregate<ForeignHandlerAggregate>();

        await Assert.That(registry.Registrations).IsEmpty();
        await Assert.That(() => registry.Get<RegisteredEvent>()).Throws<EventNotRegisteredException>();
    }

    [Test]
    public async Task Registering_an_aggregate_twice_is_idempotent()
    {
        var registry = new EventRegistry()
            .RegisterAggregate<TestAggregate>()
            .RegisterAggregate<TestAggregate>();

        await Assert.That(registry.Registrations.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Duplicate_name_across_aggregates_is_rejected()
    {
        var registry = new EventRegistry().RegisterAggregate<TestAggregate>();

        var exception = await Assert.That(() => registry.RegisterAggregate<DuplicateAggregate>())
            .Throws<DuplicateEventTypeException>();

        await Assert.That(exception!.Message).Contains("tests.registered");
    }

    [Test]
    public async Task Duplicate_name_with_different_versions_is_rejected()
    {
        await Assert.That(() => new EventRegistry().RegisterAggregate<VersionedDuplicateAggregate>())
            .Throws<DuplicateEventTypeException>();
    }

    [Test]
    public async Task Empty_event_name_is_rejected()
    {
        await Assert.That(() => new EventRegistry().RegisterAggregate<EmptyNameAggregate>())
            .Throws<InvalidEventContractException>();
    }

    [Test]
    public async Task Non_positive_event_version_is_rejected()
    {
        await Assert.That(() => new EventRegistry().RegisterAggregate<ZeroVersionAggregate>())
            .Throws<InvalidEventContractException>();
    }

    [Test]
    public async Task Unknown_event_lookup_is_rejected()
    {
        await Assert.That(() => new EventRegistry().Get("tests.unknown"))
            .Throws<EventNotRegisteredException>();
        await Assert.That(() => new EventRegistry().Get(typeof(RegisteredEvent)))
            .Throws<EventNotRegisteredException>();
    }

    private sealed record RegisteredEvent : IDomainEvent<RegisteredEvent, TestAggregate>
    {
        public static string EventType => "tests.registered";

        public static int EventVersion => 2;
    }

    private sealed record DefaultVersionEvent : IDomainEvent<DefaultVersionEvent, TestAggregate>
    {
        public static string EventType => "tests.default-version";
    }

    private readonly record struct StructEvent : IDomainEvent<StructEvent, TestAggregate>
    {
        public static string EventType => "tests.struct";
    }

    private sealed record DuplicateNamedEvent : IDomainEvent<DuplicateNamedEvent, DuplicateAggregate>
    {
        public static string EventType => "tests.registered";

        public static int EventVersion => 2;
    }

    private sealed record VersionOne : IDomainEvent<VersionOne, VersionedDuplicateAggregate>
    {
        public static string EventType => "tests.versioned";
    }

    private sealed record VersionTwo : IDomainEvent<VersionTwo, VersionedDuplicateAggregate>
    {
        public static string EventType => "tests.versioned";

        public static int EventVersion => 2;
    }

    private sealed record EmptyNameEvent : IDomainEvent<EmptyNameEvent, EmptyNameAggregate>
    {
        public static string EventType => " ";
    }

    private sealed record ZeroVersionEvent : IDomainEvent<ZeroVersionEvent, ZeroVersionAggregate>
    {
        public static string EventType => "tests.zero";

        public static int EventVersion => 0;
    }

    private sealed class TestAggregate(Guid id) : Aggregate<TestAggregate, Guid>(id),
        IApply<RegisteredEvent>,
        IApply<DefaultVersionEvent>,
        IApply<StructEvent>
    {
        void IApply<RegisteredEvent>.Apply(RegisteredEvent @event)
        {
        }

        void IApply<DefaultVersionEvent>.Apply(DefaultVersionEvent @event)
        {
        }

        void IApply<StructEvent>.Apply(StructEvent @event)
        {
        }
    }

    private sealed class ForeignHandlerAggregate(Guid id) : Aggregate<ForeignHandlerAggregate, Guid>(id),
        IApply<RegisteredEvent>
    {
        void IApply<RegisteredEvent>.Apply(RegisteredEvent @event)
        {
        }
    }

    private sealed class DuplicateAggregate(Guid id) : Aggregate<DuplicateAggregate, Guid>(id),
        IApply<DuplicateNamedEvent>
    {
        void IApply<DuplicateNamedEvent>.Apply(DuplicateNamedEvent @event)
        {
        }
    }

    private sealed class VersionedDuplicateAggregate(Guid id) : Aggregate<VersionedDuplicateAggregate, Guid>(id),
        IApply<VersionOne>,
        IApply<VersionTwo>
    {
        void IApply<VersionOne>.Apply(VersionOne @event)
        {
        }

        void IApply<VersionTwo>.Apply(VersionTwo @event)
        {
        }
    }

    private sealed class EmptyNameAggregate(Guid id) : Aggregate<EmptyNameAggregate, Guid>(id),
        IApply<EmptyNameEvent>
    {
        void IApply<EmptyNameEvent>.Apply(EmptyNameEvent @event)
        {
        }
    }

    private sealed class ZeroVersionAggregate(Guid id) : Aggregate<ZeroVersionAggregate, Guid>(id),
        IApply<ZeroVersionEvent>
    {
        void IApply<ZeroVersionEvent>.Apply(ZeroVersionEvent @event)
        {
        }
    }
}
