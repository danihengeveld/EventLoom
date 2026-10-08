using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers.Tests;

internal static class CompilationHarness
{
    public const string Usings =
        """
        using System;
        using EventLoom;

        """;

    private static readonly ImmutableArray<MetadataReference> References = CreateReferences();

    public static CSharpCompilation Compile(
        string source,
        IReadOnlyDictionary<string, ReportDiagnostic>? diagnosticOptions = null)
    {
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithNullableContextOptions(NullableContextOptions.Enable);
        if (diagnosticOptions is not null)
        {
            options = options.WithSpecificDiagnosticOptions(diagnosticOptions);
        }

        return CSharpCompilation.Create(
            "AnalyzerTest",
            [CSharpSyntaxTree.ParseText(Usings + source, new CSharpParseOptions(LanguageVersion.Latest))],
            References,
            options);
    }

    public static ImmutableArray<string> CompilerErrors(string source) =>
    [
        .. Compile(source)
            .GetDiagnostics()
            .Where(static value => value.Severity == DiagnosticSeverity.Error)
            .Select(static value => value.Id)
            .Distinct()
    ];

    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source,
        IReadOnlyDictionary<string, ReportDiagnostic>? diagnosticOptions = null)
    {
        var compilation = Compile(source, diagnosticOptions);
        var errors = compilation.GetDiagnostics()
            .Where(static value => value.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                "Test source does not compile: " +
                string.Join(Environment.NewLine, errors.Select(static e => e.ToString())));
        }

        return await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new AggregateContractAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
    }

    public static async Task<string[]> AnalyzerIdsAsync(
        string source,
        IReadOnlyDictionary<string, ReportDiagnostic>? diagnosticOptions = null) =>
    [
        .. (await AnalyzeAsync(source, diagnosticOptions)).Select(static value => value.Id)
        .Order(StringComparer.Ordinal)
    ];

    private static ImmutableArray<MetadataReference> CreateReferences()
    {
        var eventLoom = typeof(Aggregate).Assembly.Location;
        var platform = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!
            .ToString()!
            .Split(Path.PathSeparator)
            .Where(path =>
                !string.Equals(Path.GetFileName(path), Path.GetFileName(eventLoom), StringComparison.Ordinal));
        return [.. platform.Append(eventLoom).Select(static path => MetadataReference.CreateFromFile(path))];
    }
}
