namespace EventLoom.Analyzers.Tests;

/// <summary>
/// Verifies the guarantees the EventLoom contracts give through the C# compiler alone, without the analyzer.
/// </summary>
public sealed class CompilerContractTests
{
    private const string Order =
        """
        public sealed record Placed : IDomainEvent<Placed, Order>
        {
            public static string EventType => "tests.placed";
        }

        public sealed class Order(Guid id) : Aggregate<Order, Guid>(id), IApply<Placed>
        {
            public void Place() => Raise(new Placed());

            void IApply<Placed>.Apply(Placed value) { }
        }

        """;

    [Test]
    public async Task Valid_contracts_compile()
    {
        await Assert.That(CompilationHarness.CompilerErrors(Order)).IsEmpty();
    }

    [Test]
    public async Task Event_without_a_handler_on_its_owner_does_not_compile()
    {
        var errors = CompilationHarness.CompilerErrors(
            Order +
            """
            public sealed record Unhandled : IDomainEvent<Unhandled, Order>
            {
                public static string EventType => "tests.unhandled";
            }
            """);

        await Assert.That(errors).Contains("CS0311");
    }

    [Test]
    public async Task Event_without_an_event_type_does_not_compile()
    {
        var errors = CompilationHarness.CompilerErrors(
            """
            public sealed record Nameless : IDomainEvent<Nameless, Counter>;

            public sealed class Counter(Guid id) : Aggregate<Counter, Guid>(id), IApply<Nameless>
            {
                void IApply<Nameless>.Apply(Nameless value) { }
            }
            """);

        await Assert.That(errors).Contains("CS0535");
    }

    [Test]
    public async Task Event_cannot_implement_only_the_framework_ownership_interface()
    {
        var errors = CompilationHarness.CompilerErrors(
            Order +
            """
            public sealed record Sneaky : IDomainEvent<Order>;
            """);

        await Assert.That(errors).Contains("CS0535");
    }

    [Test]
    public async Task Aggregate_cannot_raise_an_event_owned_by_another_aggregate()
    {
        var errors = CompilationHarness.CompilerErrors(
            Order +
            """
            public sealed class Other(Guid id) : Aggregate<Other, Guid>(id)
            {
                public void Steal() => Raise(new Placed());
            }
            """);

        await Assert.That(errors).Contains("CS0311");
    }

    [Test]
    public async Task Framework_dispatch_member_is_not_callable()
    {
        var errors = CompilationHarness.CompilerErrors(
            Order +
            """
            public static class Bypass
            {
                public static void Run(Placed value, Order order) => ((IDomainEvent<Order>)value).ApplyTo(order);
            }
            """);

        await Assert.That(errors).Contains("CS0122");
    }

    [Test]
    public async Task Snapshot_for_an_aggregate_without_capture_and_restore_does_not_compile()
    {
        var errors = CompilationHarness.CompilerErrors(
            Order +
            """
            public sealed record OrderSnapshot : IAggregateSnapshot<OrderSnapshot, Order>
            {
                public static string SnapshotType => "tests.order";
            }
            """);

        await Assert.That(errors).Contains("CS0311");
    }

    [Test]
    public async Task Snapshot_without_a_snapshot_type_does_not_compile()
    {
        var errors = CompilationHarness.CompilerErrors(
            """
            public sealed record CounterSnapshot : IAggregateSnapshot<CounterSnapshot, Counter>;

            public sealed class Counter(Guid id) : Aggregate<Counter, Guid>(id), ISnapshotable<CounterSnapshot>
            {
                CounterSnapshot ISnapshotable<CounterSnapshot>.CreateSnapshot() => new();

                void ISnapshotable<CounterSnapshot>.RestoreSnapshot(CounterSnapshot snapshot) { }
            }
            """);

        await Assert.That(errors).Contains("CS0535");
    }
}
