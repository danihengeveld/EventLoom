using EventLoom.Testing;

namespace EventLoom.UnitTests;

public sealed class AggregateScenarioTests
{
    [Test]
    public async Task Scenario_replays_history_and_captures_raised_events()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(id => new Counter(id))
            .Given(Guid.NewGuid(), new Incremented(2))
            .When(counter => counter.Increment(3))
            .ThenEvents(events =>
            {
                if (events.Count != 1 || !Equals(events[0], new Incremented(3)))
                {
                    throw new InvalidOperationException("Unexpected events.");
                }
            });

        await Assert.That(scenario.Aggregate.Value).IsEqualTo(5);
        await Assert.That(scenario.Aggregate.Version).IsEqualTo(2);
        await Assert.That(scenario.RaisedEvents).IsEquivalentTo([(object)new Incremented(3)]);
    }

    [Test]
    public async Task Scenario_captures_rejected_commands()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(id => new Counter(id))
            .Given(Guid.NewGuid())
            .When(counter => counter.Increment(-1))
            .ThenThrows<ArgumentOutOfRangeException>()
            .ThenNoEventsRaised();

        await Assert.That(scenario.ThrownException).IsTypeOf<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Scenario_rejects_a_factory_that_raises_events()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(id =>
        {
            var counter = new Counter(id);
            counter.Increment(1);
            return counter;
        });

        await Assert.That(() => scenario.Given(Guid.NewGuid())).Throws<AggregateFactoryException>();
    }

    [Test]
    public async Task Scenario_rejects_a_factory_that_returns_null()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(_ => null!);

        await Assert.That(() => scenario.Given(Guid.NewGuid()))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("returned null");
    }

    [Test]
    public async Task Scenario_rejects_history_owned_by_another_aggregate()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(id => new Counter(id));

        await Assert.That(() => scenario.Given(Guid.NewGuid(), "not an event"))
            .Throws<EventOwnershipException>();
    }

    private sealed record Incremented(int Amount) : IDomainEvent<Incremented, Counter>
    {
        public static string EventType => "tests.scenario-incremented";
    }

    private sealed class Counter(Guid id) : Aggregate<Counter, Guid>(id), IApply<Incremented>
    {
        public int Value { get; private set; }

        public void Increment(int amount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
            Raise(new Incremented(amount));
        }

        void IApply<Incremented>.Apply(Incremented @event) => Value += @event.Amount;
    }
}
