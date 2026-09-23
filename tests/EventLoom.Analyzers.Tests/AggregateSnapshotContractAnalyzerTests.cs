using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers.Tests;

public sealed class AggregateSnapshotContractAnalyzerTests
{
    private const string Framework =
        """
        namespace EventLoom
        {
            public abstract class Aggregate { }
            public abstract class Aggregate<TId> : Aggregate
            {
                protected Aggregate(TId id) { }
            }

            public interface IAggregateSnapshot<out TAggregate> where TAggregate : Aggregate { }
            public sealed class SnapshotTypeAttribute : System.Attribute
            {
                public SnapshotTypeAttribute(string name) { }
            }
        }

        """;

    [Test]
    public async Task ReportsConcreteSnapshotWithoutAttribute()
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

            public sealed record CounterSnapshot : EventLoom.IAggregateSnapshot<Counter>;
            """);

        await Assert.That(diagnostics.Select(value => value.Id))
            .IsEquivalentTo([AggregateSnapshotContractAnalyzer.MissingSnapshotTypeDiagnosticId]);
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
            AggregateSnapshotContractAnalyzer.MissingSnapshotCreateDiagnosticId,
            AggregateSnapshotContractAnalyzer.MissingSnapshotRestoreDiagnosticId
        ]);
    }

    [Test]
    public async Task ReportsInvalidSnapshotMethodSignatures()
    {
        var diagnostics = await AnalyzeAsync(
            Framework +
            """
            public sealed class Counter : EventLoom.Aggregate<int>
            {
                public Counter(int id) : base(id) { }
                public static CounterSnapshot CreateSnapshot() => new();
                public int RestoreSnapshot(CounterSnapshot snapshot) => 0;
            }

            [EventLoom.SnapshotType("tests.counter")]
            public sealed record CounterSnapshot : EventLoom.IAggregateSnapshot<Counter>;
            """);

        await Assert.That(diagnostics.Select(value => value.Id)).IsEquivalentTo(
        [
            AggregateSnapshotContractAnalyzer.InvalidSnapshotCreateDiagnosticId,
            AggregateSnapshotContractAnalyzer.InvalidSnapshotRestoreDiagnosticId
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
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AggregateSnapshotContractAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
    }
}
