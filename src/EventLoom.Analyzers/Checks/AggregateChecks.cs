using System.Linq;
using EventLoom.Analyzers.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers.Checks;

/// <summary>Checks that apply to aggregate classes themselves.</summary>
internal static class AggregateChecks
{
    internal static void Analyze(
        SymbolAnalysisContext context,
        KnownSymbols symbols,
        INamedTypeSymbol type,
        INamedTypeSymbol aggregateBase,
        Location location)
    {
        var self = aggregateBase.TypeArguments[0];
        var validSelf = SymbolEqualityComparer.Default.Equals(self, type) ||
                        (type.IsAbstract && self is ITypeParameterSymbol);
        if (!validSelf)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.InvalidSelfType,
                location,
                type.Name,
                self.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }

        if (type.IsAbstract)
        {
            return;
        }

        var ownedContract = symbols.DomainEventOwner.Construct(type);
        foreach (var handler in type.AllInterfaces.Where(value =>
                     SymbolEqualityComparer.Default.Equals(value.OriginalDefinition, symbols.Apply)))
        {
            var eventType = handler.TypeArguments[0];
            if (!eventType.AllInterfaces.Contains(ownedContract, SymbolEqualityComparer.Default))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.ForeignApplyHandler,
                    location,
                    type.Name,
                    eventType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }
    }
}
