using System.Collections.Immutable;
using EventLoom.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers.Tests;

public sealed class DomainEventContractAnalyzerTests
{
    [Test]
    public async Task ReportsConcreteDomainEventWithoutAttribute()
    {
        var diagnostics = await AnalyzeAsync(
            """
            namespace EventLoom
            {
                public interface IDomainEvent { }
                public sealed class EventTypeAttribute : System.Attribute { public EventTypeAttribute(string name) { } }
            }
            public sealed record MissingName : EventLoom.IDomainEvent;
            """);

        await Assert.That(diagnostics.Single().Id)
            .IsEqualTo(DomainEventContractAnalyzer.MissingEventTypeDiagnosticId);
    }

    [Test]
    public async Task AcceptsDomainEventWithStableIdentity()
    {
        var diagnostics = await AnalyzeAsync(
            """
            namespace EventLoom
            {
                public interface IDomainEvent { }
                public sealed class EventTypeAttribute : System.Attribute { public EventTypeAttribute(string name) { } }
            }
            [EventLoom.EventType("tests.named")]
            public sealed record NamedEvent : EventLoom.IDomainEvent;
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
