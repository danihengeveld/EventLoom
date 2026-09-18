using EventLoom;

namespace EventLoom.UnitTests;

public sealed class EventRegistryTests
{
    [Test]
    public async Task Register_event_exposes_stable_metadata()
    {
        var registry = new EventRegistry().RegisterEvent<RegisteredEvent>();

        var registration = registry.Get("tests.registered", 2);

        await Assert.That(registration.ClrType).IsEqualTo(typeof(RegisteredEvent));
        await Assert.That(registry.Get<RegisteredEvent>()).IsEqualTo(registration);
    }

    [Test]
    public async Task Duplicate_clr_registration_is_rejected()
    {
        var registry = new EventRegistry().RegisterEvent<RegisteredEvent>();

        await Assert.That(() => registry.RegisterEvent<RegisteredEvent>())
            .Throws<DuplicateEventRegistrationException>();
    }

    [Test]
    public async Task Duplicate_name_and_version_is_rejected()
    {
        var registry = new EventRegistry().RegisterEvent<RegisteredEvent>();

        await Assert.That(() => registry.RegisterEvent<DuplicateNamedEvent>())
            .Throws<DuplicateEventTypeException>();
    }

    [Test]
    public async Task Missing_metadata_is_rejected()
    {
        await Assert.That(() => new EventRegistry().RegisterEvent<MissingMetadataEvent>())
            .Throws<EventTypeMetadataMissingException>();
    }

    [Test]
    public async Task Unknown_event_lookup_is_rejected()
    {
        await Assert.That(() => new EventRegistry().Get("tests.unknown", 1))
            .Throws<EventNotRegisteredException>();
    }

    [EventType("tests.registered", Version = 2)]
    private sealed record RegisteredEvent : IDomainEvent<TestAggregate>;

    [EventType("tests.registered", Version = 2)]
    private sealed record DuplicateNamedEvent : IDomainEvent<TestAggregate>;

    private sealed record MissingMetadataEvent : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(RegisteredEvent @event) { }
        private void Apply(DuplicateNamedEvent @event) { }
        private void Apply(MissingMetadataEvent @event) { }
    }
}
