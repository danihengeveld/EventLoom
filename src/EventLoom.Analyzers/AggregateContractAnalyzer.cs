using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using EventLoom.Analyzers.Checks;
using EventLoom.Analyzers.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers;

/// <summary>
/// Reports EventLoom aggregate, event, and snapshot rules that the C# type system cannot express.
/// </summary>
/// <remarks>
/// This class only wires the rules to the compiler. The rules live in <c>Checks</c>, grouped by what they inspect;
/// <c>Descriptors</c> defines the diagnostics and <c>Symbols</c> resolves the EventLoom types once per compilation.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateContractAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            Descriptors.InvalidSelfType,
            Descriptors.RaiseInHandler,
            Descriptors.RaiseInConstructor,
            Descriptors.DirectHandlerCall,
            Descriptors.NondeterministicHandler,
            Descriptors.NonConstantIdentity,
            Descriptors.DuplicateIdentity,
            Descriptors.MutableContract,
            Descriptors.ForeignApplyHandler,
            Descriptors.StateChangedOutsideApply);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var symbols = KnownSymbols.Create(startContext.Compilation);
            if (symbols is null)
            {
                return;
            }

            var identities = new ConcurrentBag<DeclaredIdentity>();

            startContext.RegisterSymbolAction(value => AnalyzeType(value, symbols), SymbolKind.NamedType);
            startContext.RegisterSymbolAction(value => HandlerChecks.AnalyzeMethod(value, symbols), SymbolKind.Method);

            startContext.RegisterOperationAction(
                value => HandlerChecks.AnalyzeInvocation(value, symbols),
                OperationKind.Invocation);
            startContext.RegisterOperationAction(
                value => HandlerChecks.AnalyzeMemberUse(value, symbols),
                OperationKind.PropertyReference,
                OperationKind.FieldReference,
                OperationKind.MethodReference,
                OperationKind.ObjectCreation);
            startContext.RegisterOperationAction(
                value => HandlerChecks.AnalyzeAssignment(value, symbols),
                OperationKind.SimpleAssignment,
                OperationKind.CompoundAssignment,
                OperationKind.CoalesceAssignment,
                OperationKind.Increment,
                OperationKind.Decrement);

            startContext.RegisterOperationBlockAction(
                value => IdentityChecks.AnalyzeIdentityGetter(value, symbols, identities));
            startContext.RegisterOperationAction(
                value => IdentityChecks.AnalyzeIdentityInitializer(value, symbols, identities),
                OperationKind.PropertyInitializer);
            startContext.RegisterCompilationEndAction(
                value => IdentityChecks.ReportDuplicateIdentities(value, identities));
        });
    }

    private static void AnalyzeType(SymbolAnalysisContext context, KnownSymbols symbols)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var location = type.Locations.FirstOrDefault(static value => value.IsInSource);
        if (location is null)
        {
            return;
        }

        var aggregateBase = symbols.FindAggregateBase(type);
        if (aggregateBase is not null && type.TypeKind == TypeKind.Class)
        {
            AggregateChecks.Analyze(context, symbols, type, aggregateBase, location);
        }

        foreach (var contract in symbols.GetIdentityContracts(type))
        {
            ContractChecks.AnalyzeMutability(context, type);
            ContractChecks.AnalyzeAutoPropertyIdentity(context, type, contract);
            break;
        }
    }
}
