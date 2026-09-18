using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers.Tests;

public sealed class DomainEventContractAnalyzerTests
{
    private const string Framework =
        """
        namespace EventLoom
        {
            public abstract class Aggregate { }
            public abstract class Aggregate<TId> : Aggregate
            {
                protected Aggregate(TId id) { }
                protected void Raise<TEvent>(TEvent value) { }
            }

            public interface IDomainEvent<out TAggregate> where TAggregate : Aggregate { }
            public sealed class EventTypeAttribute : System.Attribute
            {
                public EventTypeAttribute(string name) { }
            }

            public interface IAggregateSnapshot<out TAggregate> where TAggregate : Aggregate { }
            public sealed class SnapshotTypeAttribute : System.Attribute
            {
                public SnapshotTypeAttribute(string name) { }
            }
        }

        """;

    [Test]
    public async Task ReportsConcreteDomainEventWithoutAttribute()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public sealed class Counter : EventLoom.Aggregate<int>
            {
                public Counter(int id) : base(id) { }
                private void Apply(MissingName value) { }
            }

            public sealed record MissingName : EventLoom.IDomainEvent<Counter>;
            """);

        await Assert.That(diagnostics.Select(value => value.Id))
            .IsEquivalentTo([DomainEventContractAnalyzer.MissingEventTypeDiagnosticId]);
    }

    [Test]
    public async Task AcceptsOwnedEventWithValidInheritedHandler()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public abstract class CounterBase : EventLoom.Aggregate<int>
            {
                protected CounterBase(int id) : base(id) { }
                protected void Apply(Incremented value) { }
            }

            public sealed class Counter : CounterBase
            {
                public Counter(int id) : base(id) { }
                public void Increment() => Raise(new Incremented());
            }

            [EventLoom.EventType("tests.incremented")]
            public sealed record Incremented : EventLoom.IDomainEvent<CounterBase>;
            """);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ReportsMissingApplyHandler()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public sealed class Counter : EventLoom.Aggregate<int>
            {
                public Counter(int id) : base(id) { }
            }

            [EventLoom.EventType("tests.incremented")]
            public sealed record Incremented : EventLoom.IDomainEvent<Counter>;
            """);

        await Assert.That(diagnostics.Select(value => value.Id))
            .Contains(DomainEventContractAnalyzer.MissingApplyHandlerDiagnosticId);
    }

    [Test]
    public async Task ReportsInvalidApplyHandler()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public sealed class Counter : EventLoom.Aggregate<int>
            {
                public Counter(int id) : base(id) { }
                public static int Apply(Incremented value) => 0;
            }

            [EventLoom.EventType("tests.incremented")]
            public sealed record Incremented : EventLoom.IDomainEvent<Counter>;
            """);

        await Assert.That(diagnostics.Select(value => value.Id))
            .Contains(DomainEventContractAnalyzer.InvalidApplyHandlerDiagnosticId);
    }

    [Test]
    public async Task ReportsHandlersDuplicatedAcrossAggregateHierarchy()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public abstract class CounterBase : EventLoom.Aggregate<int>
            {
                protected CounterBase(int id) : base(id) { }
                protected void Apply(Incremented value) { }
            }

            public sealed class Counter : CounterBase
            {
                public Counter(int id) : base(id) { }
                private new void Apply(Incremented value) { }
            }

            [EventLoom.EventType("tests.incremented")]
            public sealed record Incremented : EventLoom.IDomainEvent<Counter>;
            """);

        await Assert.That(diagnostics.Select(value => value.Id))
            .Contains(DomainEventContractAnalyzer.AmbiguousApplyHandlerDiagnosticId);
    }

    [Test]
    public async Task ReportsEventRaisedByWrongAggregate()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public sealed class Order : EventLoom.Aggregate<int>
            {
                public Order(int id) : base(id) { }
                private void Apply(OrderPlaced value) { }
            }

            public sealed class Cart : EventLoom.Aggregate<int>
            {
                public Cart(int id) : base(id) { }
                public void PlaceOrder() => Raise(new OrderPlaced());
            }

            [EventLoom.EventType("tests.order-placed")]
            public sealed record OrderPlaced : EventLoom.IDomainEvent<Order>;
            """);

        await Assert.That(diagnostics.Select(value => value.Id))
            .Contains(DomainEventContractAnalyzer.WrongAggregateOwnerDiagnosticId);
    }

    [Test]
    public async Task ReportsMissingPrivateSnapshotMethods()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public sealed class Counter : EventLoom.Aggregate<int>
            {
                public Counter(int id) : base(id) { }
            }

            [EventLoom.SnapshotType("tests.counter")]
            public sealed record CounterSnapshot : EventLoom.IAggregateSnapshot<Counter>;
            """);

        await Assert.That(diagnostics.Select(value => value.Id)).IsEquivalentTo(
            [
                DomainEventContractAnalyzer.MissingSnapshotCreateDiagnosticId,
                DomainEventContractAnalyzer.MissingSnapshotRestoreDiagnosticId
            ]);
    }

    [Test]
    public async Task AcceptsSnapshotWithPrivateCreationAndRestoreMethods()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public sealed class Counter : EventLoom.Aggregate<int>
            {
                public Counter(int id) : base(id) { }
                private CounterSnapshot CreateSnapshot() => new();
                private void RestoreSnapshot(CounterSnapshot snapshot) { }
            }

            [EventLoom.SnapshotType("tests.counter")]
            public sealed record CounterSnapshot : EventLoom.IAggregateSnapshot<Counter>;
            """);

        await Assert.That(diagnostics).IsEmpty();
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!
            .ToString()!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "AnalyzerTest",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new DomainEventContractAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
    }
}
