using Microsoft.CodeAnalysis;

namespace EventLoom.Analyzers.Tests;

public sealed class AggregateContractAnalyzerTests
{
    private const string Contracts =
        """
        public sealed record Placed(string Sku) : IDomainEvent<Placed, Order>
        {
            public static string EventType => "tests.placed";
        }

        public sealed record Cancelled : IDomainEvent<Cancelled, Order>
        {
            public const string Name = "tests.cancelled";

            public static string EventType => Name;

            public static int EventVersion => 2;
        }

        public readonly record struct Archived : IDomainEvent<Archived, Order>
        {
            public static string EventType { get; } = "tests.archived";
        }

        public sealed record OrderSnapshot(string? Sku) : IAggregateSnapshot<OrderSnapshot, Order>
        {
            public static string SnapshotType => "tests.placed";
        }

        """;

    private const string ValidOrder =
        Contracts +
        """
        public sealed class Order(Guid id) : Aggregate<Order, Guid>(id),
            IApply<Placed>, IApply<Cancelled>, IApply<Archived>, ISnapshotable<OrderSnapshot>
        {
            private string? sku;

            public DateTimeOffset PlacedAt { get; private set; }

            public void Place(string sku) => Raise(new Placed(sku));

            public void Cancel() => Raise(new Cancelled());

            public void Archive() => Raise(new Archived());

            void IApply<Placed>.Apply(Placed value) => sku = value.Sku;

            void IApply<Cancelled>.Apply(Cancelled value) => sku = null;

            void IApply<Archived>.Apply(Archived value) { }

            OrderSnapshot ISnapshotable<OrderSnapshot>.CreateSnapshot() => new(sku);

            void ISnapshotable<OrderSnapshot>.RestoreSnapshot(OrderSnapshot snapshot) => sku = snapshot.Sku;
        }

        public static class Commands
        {
            public static DateTime Now() => DateTime.UtcNow;
        }
        """;

    [Test]
    public async Task Valid_aggregate_has_no_diagnostics()
    {
        await Assert.That(await CompilationHarness.AnalyzerIdsAsync(ValidOrder)).IsEmpty();
    }

    [Test]
    public async Task Diagnostics_have_the_documented_severities()
    {
        var descriptors = new AggregateContractAnalyzer().SupportedDiagnostics.ToDictionary(value => value.Id);

        foreach (var id in new[] { "EL0101", "EL0102", "EL0103", "EL0104", "EL0105", "EL0106", "EL0107", "EL0108" })
        {
            await Assert.That(descriptors[id].DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
            await Assert.That(descriptors[id].IsEnabledByDefault).IsTrue();
        }

        await Assert.That(descriptors["EL0109"].DefaultSeverity).IsEqualTo(DiagnosticSeverity.Info);
        await Assert.That(descriptors["EL0109"].IsEnabledByDefault).IsTrue();
        await Assert.That(descriptors["EL0110"].DefaultSeverity).IsEqualTo(DiagnosticSeverity.Info);
        await Assert.That(descriptors["EL0110"].IsEnabledByDefault).IsFalse();
    }

    [Test]
    public async Task EL0101_reports_aggregate_that_passes_another_type_as_self()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            ValidOrder +
            """

            public sealed class Impostor(Guid id) : Aggregate<Order, Guid>(id);
            """);

        await Assert.That(ids).IsEquivalentTo([AggregateContractAnalyzer.InvalidSelfTypeDiagnosticId]);
    }

    [Test]
    public async Task EL0101_accepts_generic_abstract_base_aggregates()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            """
            public abstract class AuditedAggregate<TSelf>(Guid id) : Aggregate<TSelf, Guid>(id)
                where TSelf : AuditedAggregate<TSelf>;

            public sealed record Opened : IDomainEvent<Opened, Account>
            {
                public static string EventType => "tests.opened";
            }

            public sealed class Account(Guid id) : AuditedAggregate<Account>(id), IApply<Opened>
            {
                void IApply<Opened>.Apply(Opened value) { }
            }
            """);

        await Assert.That(ids).IsEmpty();
    }

    [Test]
    public async Task EL0102_reports_raise_inside_event_and_snapshot_handlers()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            Contracts +
            """
            public sealed class Order(Guid id) : Aggregate<Order, Guid>(id),
                IApply<Placed>, IApply<Cancelled>, IApply<Archived>, ISnapshotable<OrderSnapshot>
            {
                void IApply<Placed>.Apply(Placed value) => Raise(new Archived());

                public void Apply(Cancelled value) => Raise(new Archived());

                void IApply<Archived>.Apply(Archived value) { }

                OrderSnapshot ISnapshotable<OrderSnapshot>.CreateSnapshot()
                {
                    Raise(new Archived());
                    return new(null);
                }

                void ISnapshotable<OrderSnapshot>.RestoreSnapshot(OrderSnapshot snapshot) => Raise(new Archived());
            }
            """);

        await Assert.That(ids).IsEquivalentTo(Enumerable.Repeat(AggregateContractAnalyzer.RaiseInHandlerDiagnosticId, 4));
    }

    [Test]
    public async Task EL0103_reports_raise_in_aggregate_constructor()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            """
            public sealed record Created : IDomainEvent<Created, Counter>
            {
                public static string EventType => "tests.created";
            }

            public sealed class Counter : Aggregate<Counter, Guid>, IApply<Created>
            {
                public Counter(Guid id) : base(id) => Raise(new Created());

                public static Counter Create(Guid id)
                {
                    var counter = new Counter(id);
                    return counter;
                }

                void IApply<Created>.Apply(Created value) { }
            }
            """);

        await Assert.That(ids).IsEquivalentTo([AggregateContractAnalyzer.RaiseInConstructorDiagnosticId]);
    }

    [Test]
    public async Task EL0104_reports_direct_handler_calls()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            Contracts +
            """
            public sealed class Order(Guid id) : Aggregate<Order, Guid>(id),
                IApply<Placed>, IApply<Cancelled>, IApply<Archived>, ISnapshotable<OrderSnapshot>
            {
                public void Apply(Placed value) { }

                void IApply<Cancelled>.Apply(Cancelled value) { }

                void IApply<Archived>.Apply(Archived value) { }

                OrderSnapshot ISnapshotable<OrderSnapshot>.CreateSnapshot() => new(null);

                void ISnapshotable<OrderSnapshot>.RestoreSnapshot(OrderSnapshot snapshot) { }
            }

            public static class Bypass
            {
                public static void Run(Order order)
                {
                    order.Apply(new Placed("sku"));
                    ((IApply<Cancelled>)order).Apply(new Cancelled());
                    ((ISnapshotable<OrderSnapshot>)order).RestoreSnapshot(new OrderSnapshot("sku"));
                }
            }
            """);

        await Assert.That(ids).IsEquivalentTo(Enumerable.Repeat(AggregateContractAnalyzer.DirectHandlerCallDiagnosticId, 3));
    }

    [Test]
    public async Task EL0105_reports_nondeterministic_apis_in_handlers()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            Contracts +
            """
            public sealed class Order(Guid id) : Aggregate<Order, Guid>(id),
                IApply<Placed>, IApply<Cancelled>, IApply<Archived>, ISnapshotable<OrderSnapshot>
            {
                private DateTime placedAt;
                private Guid token;
                private int roll;

                void IApply<Placed>.Apply(Placed value) => placedAt = DateTime.UtcNow;

                void IApply<Cancelled>.Apply(Cancelled value) => token = Guid.NewGuid();

                void IApply<Archived>.Apply(Archived value) => roll = new Random().Next();

                OrderSnapshot ISnapshotable<OrderSnapshot>.CreateSnapshot() => new(Environment.MachineName);

                async void ISnapshotable<OrderSnapshot>.RestoreSnapshot(OrderSnapshot snapshot) =>
                    await System.Threading.Tasks.Task.Yield();
            }
            """);

        await Assert.That(ids.Distinct()).IsEquivalentTo([AggregateContractAnalyzer.NondeterministicHandlerDiagnosticId]);
        await Assert.That(ids.Length).IsGreaterThanOrEqualTo(6);
    }

    [Test]
    public async Task EL0106_reports_non_constant_or_invalid_identities()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            """
            public sealed record Computed : IDomainEvent<Computed, Counter>
            {
                public static string EventType => Prefix() + "computed";

                private static string Prefix() => "tests.";
            }

            public sealed record Empty : IDomainEvent<Empty, Counter>
            {
                public static string EventType => " ";
            }

            public sealed record ZeroVersion : IDomainEvent<ZeroVersion, Counter>
            {
                public static string EventType => "tests.zero";

                public static int EventVersion => 0;
            }

            public sealed record Unassigned : IDomainEvent<Unassigned, Counter>
            {
                public static string EventType { get; } = null!;
            }

            public sealed record AutoProperty : IDomainEvent<AutoProperty, Counter>
            {
                public static string EventType { get; }
            }

            public sealed class Counter(Guid id) : Aggregate<Counter, Guid>(id),
                IApply<Computed>, IApply<Empty>, IApply<ZeroVersion>, IApply<Unassigned>, IApply<AutoProperty>
            {
                void IApply<Computed>.Apply(Computed value) { }
                void IApply<Empty>.Apply(Empty value) { }
                void IApply<ZeroVersion>.Apply(ZeroVersion value) { }
                void IApply<Unassigned>.Apply(Unassigned value) { }
                void IApply<AutoProperty>.Apply(AutoProperty value) { }
            }
            """);

        await Assert.That(ids).IsEquivalentTo(Enumerable.Repeat(AggregateContractAnalyzer.NonConstantIdentityDiagnosticId, 5));
    }

    [Test]
    public async Task EL0107_reports_duplicate_event_names_but_not_across_kinds()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            ValidOrder +
            """

            public sealed record PlacedAgain : IDomainEvent<PlacedAgain, Other>
            {
                public static string EventType => "tests.placed";

                public static int EventVersion => 2;
            }

            public sealed class Other(Guid id) : Aggregate<Other, Guid>(id), IApply<PlacedAgain>
            {
                void IApply<PlacedAgain>.Apply(PlacedAgain value) { }
            }
            """);

        await Assert.That(ids).IsEquivalentTo(Enumerable.Repeat(AggregateContractAnalyzer.DuplicateIdentityDiagnosticId, 2));
    }

    [Test]
    public async Task EL0108_reports_mutable_events_and_snapshots()
    {
        var ids = await CompilationHarness.AnalyzerIdsAsync(
            """
            public sealed class Mutable : IDomainEvent<Mutable, Counter>
            {
                public static string EventType => "tests.mutable";

                public int Settable { get; set; }

                public int Field;

                public int InitOnly { get; init; }

                public readonly int ReadOnlyField = 1;
            }

            public record struct MutableSnapshot(int Value) : IAggregateSnapshot<MutableSnapshot, Counter>
            {
                public static string SnapshotType => "tests.counter";
            }

            public sealed class Counter(Guid id) : Aggregate<Counter, Guid>(id),
                IApply<Mutable>, ISnapshotable<MutableSnapshot>
            {
                void IApply<Mutable>.Apply(Mutable value) { }

                MutableSnapshot ISnapshotable<MutableSnapshot>.CreateSnapshot() => new(0);

                void ISnapshotable<MutableSnapshot>.RestoreSnapshot(MutableSnapshot snapshot) { }
            }
            """);

        await Assert.That(ids).IsEquivalentTo(Enumerable.Repeat(AggregateContractAnalyzer.MutableContractDiagnosticId, 3));
    }

    [Test]
    public async Task EL0109_reports_handler_for_an_event_owned_by_another_aggregate()
    {
        var diagnostics = await CompilationHarness.AnalyzeAsync(
            ValidOrder +
            """

            public sealed class Other(Guid id) : Aggregate<Other, Guid>(id), IApply<Placed>
            {
                void IApply<Placed>.Apply(Placed value) { }
            }
            """);

        await Assert.That(diagnostics.Select(value => value.Id))
            .IsEquivalentTo([AggregateContractAnalyzer.ForeignApplyHandlerDiagnosticId]);
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Info);
    }

    [Test]
    public async Task EL0110_is_off_by_default_and_reports_state_changed_outside_handlers_when_enabled()
    {
        const string source =
            """
            public sealed record Incremented : IDomainEvent<Incremented, Counter>
            {
                public static string EventType => "tests.incremented";
            }

            public sealed class Counter : Aggregate<Counter, Guid>, IApply<Incremented>
            {
                private int value;

                public Counter(Guid id) : base(id) => value = 0;

                public void Increment()
                {
                    value++;
                    Raise(new Incremented());
                }

                void IApply<Incremented>.Apply(Incremented @event) => value++;
            }
            """;

        var defaults = await CompilationHarness.AnalyzerIdsAsync(source);
        var enabled = await CompilationHarness.AnalyzerIdsAsync(
            source,
            new Dictionary<string, ReportDiagnostic>
            {
                [AggregateContractAnalyzer.StateChangedOutsideApplyDiagnosticId] = ReportDiagnostic.Info
            });

        await Assert.That(defaults).IsEmpty();
        await Assert.That(enabled).IsEquivalentTo([AggregateContractAnalyzer.StateChangedOutsideApplyDiagnosticId]);
    }
}
