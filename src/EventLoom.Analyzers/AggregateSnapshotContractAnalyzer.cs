using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateSnapshotContractAnalyzer : DiagnosticAnalyzer
{
    public const string MissingSnapshotTypeDiagnosticId = "EL0006";
    public const string MissingSnapshotCreateDiagnosticId = "EL0007";
    public const string InvalidSnapshotCreateDiagnosticId = "EL0008";
    public const string MissingSnapshotRestoreDiagnosticId = "EL0009";
    public const string InvalidSnapshotRestoreDiagnosticId = "EL0010";

    private static readonly DiagnosticDescriptor MissingSnapshotType = new(
        MissingSnapshotTypeDiagnosticId,
        "Aggregate snapshot requires a persisted identity",
        "Aggregate snapshot '{0}' must declare SnapshotTypeAttribute",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
        "Every persisted EventLoom aggregate snapshot needs a stable SnapshotTypeAttribute name and version.");

    private static readonly DiagnosticDescriptor MissingSnapshotCreate = new(
        MissingSnapshotCreateDiagnosticId,
        "Aggregate snapshot requires a creation method",
        "Aggregate '{0}' must declare private {1} CreateSnapshot()",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A snapshot-owning aggregate must provide the private creation method used by runtime dispatch.");

    private static readonly DiagnosticDescriptor InvalidSnapshotCreate = new(
        InvalidSnapshotCreateDiagnosticId,
        "Aggregate snapshot creation method has an invalid signature",
        "CreateSnapshot on aggregate '{0}' must be a private, non-static, non-generic method returning {1} with no parameters",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Snapshot creation methods must match EventLoom's runtime dispatch contract.");

    private static readonly DiagnosticDescriptor MissingSnapshotRestore = new(
        MissingSnapshotRestoreDiagnosticId,
        "Aggregate snapshot requires a restoration method",
        "Aggregate '{0}' must declare private void RestoreSnapshot({1})",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
        "A snapshot-owning aggregate must provide the private restoration method used by runtime dispatch.");

    private static readonly DiagnosticDescriptor InvalidSnapshotRestore = new(
        InvalidSnapshotRestoreDiagnosticId,
        "Aggregate snapshot restoration method has an invalid signature",
        "RestoreSnapshot on aggregate '{0}' must be a private, non-static, non-generic void method accepting {1}",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Snapshot restoration methods must match EventLoom's runtime dispatch contract.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            MissingSnapshotType,
            MissingSnapshotCreate,
            InvalidSnapshotCreate,
            MissingSnapshotRestore,
            InvalidSnapshotRestore);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var aggregateSnapshot = startContext.Compilation.GetTypeByMetadataName("EventLoom.IAggregateSnapshot`1");
            var snapshotTypeAttribute =
                startContext.Compilation.GetTypeByMetadataName("EventLoom.SnapshotTypeAttribute");
            if (aggregateSnapshot is null || snapshotTypeAttribute is null)
            {
                return;
            }

            startContext.RegisterSymbolAction(
                innerContext => AnalyzeNamedType(innerContext, aggregateSnapshot, snapshotTypeAttribute),
                SymbolKind.NamedType);
        });
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol aggregateSnapshot,
        INamedTypeSymbol snapshotTypeAttribute)
    {
        var snapshot = (INamedTypeSymbol)context.Symbol;
        if (snapshot.TypeKind is not (TypeKind.Class or TypeKind.Struct) || snapshot.IsAbstract)
        {
            return;
        }

        var contract = snapshot.AllInterfaces.FirstOrDefault(interfaceType =>
            SymbolEqualityComparer.Default.Equals(interfaceType.OriginalDefinition, aggregateSnapshot));
        if (contract is null || contract.TypeArguments[0] is not INamedTypeSymbol owner)
        {
            return;
        }

        var location = snapshot.Locations.FirstOrDefault(static value => value.IsInSource);
        if (location is null)
        {
            return;
        }

        if (!snapshot.GetAttributes().Any(attribute =>
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, snapshotTypeAttribute)))
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingSnapshotType, location, snapshot.Name));
        }

        var createMethods = owner.GetMembers("CreateSnapshot").OfType<IMethodSymbol>().ToArray();
        if (createMethods.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingSnapshotCreate, location, owner.Name, snapshot.Name));
        }
        else if (createMethods.Any(method => !IsValidSnapshotCreate(method, snapshot)))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidSnapshotCreate, location, owner.Name, snapshot.Name));
        }

        var restoreMethods = owner.GetMembers("RestoreSnapshot").OfType<IMethodSymbol>().ToArray();
        if (restoreMethods.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingSnapshotRestore, location, owner.Name, snapshot.Name));
        }
        else if (restoreMethods.Any(method => !IsValidSnapshotRestore(method, snapshot)))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidSnapshotRestore, location, owner.Name, snapshot.Name));
        }
    }

    private static bool IsValidSnapshotCreate(IMethodSymbol method, INamedTypeSymbol snapshot) =>
        method.MethodKind == MethodKind.Ordinary &&
        !method.IsStatic &&
        !method.IsGenericMethod &&
        method.DeclaredAccessibility == Accessibility.Private &&
        method.Parameters.Length == 0 &&
        SymbolEqualityComparer.Default.Equals(method.ReturnType, snapshot);

    private static bool IsValidSnapshotRestore(IMethodSymbol method, INamedTypeSymbol snapshot) =>
        method.MethodKind == MethodKind.Ordinary &&
        !method.IsStatic &&
        !method.IsGenericMethod &&
        method.DeclaredAccessibility == Accessibility.Private &&
        method.ReturnsVoid &&
        method.Parameters.Length == 1 &&
        SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, snapshot);
}
