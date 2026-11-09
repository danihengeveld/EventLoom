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
            .ThenRaised(new Incremented(3));

        await Assert.That(scenario.Aggregate.Value).IsEqualTo(5);
        await Assert.That(scenario.Aggregate.Version).IsEqualTo(2);
        await Assert.That(scenario.RaisedEvents).IsEquivalentTo([(object)new Incremented(3)]);
    }

    [Test]
    public async Task Async_assertions_are_awaited_and_their_failures_propagate()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(id => new Counter(id))
            .Given(Guid.NewGuid(), new Incremented(2))
            .When(counter => counter.Increment(3));

        await scenario.ThenEventsAsync(async events => await Assert.That(events).Count().IsEqualTo(1));
        await scenario.ThenAsync(async counter => await Assert.That(counter.Value).IsEqualTo(5));
        await Assert.That(async () => await scenario.ThenEventsAsync(async _ =>
            {
                await Task.Yield();
                throw new TimeoutException("late assertion failure");
            }))
            .Throws<TimeoutException>();
        await Assert.That(async () => await scenario.ThenAsync(async _ =>
            {
                await Task.Yield();
                throw new TimeoutException("late assertion failure");
            }))
            .Throws<TimeoutException>();
    }

    [Test]
    public async Task Then_raised_rejects_different_or_missing_events()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(id => new Counter(id))
            .Given(Guid.NewGuid())
            .When(counter => counter.Increment(3));

        await Assert.That(() => scenario.ThenRaised(new Incremented(4)))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("Incremented { Amount = 3 }");
        await Assert.That(() => scenario.ThenRaised(new Incremented(3), new Incremented(3)))
            .Throws<InvalidOperationException>();
        await Assert.That(() => scenario.ThenRaised())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Unexpected_command_exceptions_fail_every_other_assertion()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(id => new Counter(id))
            .Given(Guid.NewGuid())
            .When(counter => counter.Increment(-1));

        await Assert.That(() => scenario.ThenNoEventsRaised())
            .Throws<InvalidOperationException>()
            .WithMessageContaining("ArgumentOutOfRangeException");
        await Assert.That(() => scenario.Then(_ => { })).Throws<InvalidOperationException>();
        await Assert.That(() => scenario.ThenEvents(_ => { })).Throws<InvalidOperationException>();
        await Assert.That(() => scenario.ThenRaised()).Throws<InvalidOperationException>();
        await Assert.That(async () => await scenario.ThenAsync(_ => Task.CompletedTask))
            .Throws<InvalidOperationException>();
        await Assert.That(async () => await scenario.ThenEventsAsync(_ => Task.CompletedTask))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_new_command_resets_the_observed_exception()
    {
        var scenario = AggregateScenario.For<Counter, Guid>(id => new Counter(id))
            .Given(Guid.NewGuid())
            .When(counter => counter.Increment(-1))
            .ThenThrows<ArgumentOutOfRangeException>()
            .When(counter => counter.Increment(2))
            .ThenRaised(new Incremented(2));

        await Assert.That(scenario.ThrownException).IsNull();
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
