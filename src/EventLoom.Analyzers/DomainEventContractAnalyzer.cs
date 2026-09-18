using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainEventContractAnalyzer : DiagnosticAnalyzer
{
    public const string MissingEventTypeDiagnosticId = "EL0001";

    private static readonly DiagnosticDescriptor MissingEventType = new(
        MissingEventTypeDiagnosticId,
        "Domain event requires a persisted identity",
        "Domain event '{0}' must declare EventTypeAttribute",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every persisted EventLoom domain event needs a stable EventTypeAttribute name and version.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MissingEventType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var domainEvent = startContext.Compilation.GetTypeByMetadataName("EventLoom.IDomainEvent");
            var eventTypeAttribute = startContext.Compilation.GetTypeByMetadataName("EventLoom.EventTypeAttribute");
            if (domainEvent is null || eventTypeAttribute is null)
            {
                return;
            }

            startContext.RegisterSymbolAction(
                symbolContext => AnalyzeNamedType(symbolContext, domainEvent, eventTypeAttribute),
                SymbolKind.NamedType);
        });
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol domainEvent,
        INamedTypeSymbol eventTypeAttribute)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) ||
            type.IsAbstract ||
            !type.AllInterfaces.Contains(domainEvent, SymbolEqualityComparer.Default) ||
            type.GetAttributes().Any(attribute =>
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, eventTypeAttribute)))
        {
            return;
        }

        var location = type.Locations.FirstOrDefault(static value => value.IsInSource);
        if (location is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingEventType, location, type.Name));
        }
    }
}
