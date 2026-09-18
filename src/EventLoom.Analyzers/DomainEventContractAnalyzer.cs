using System.Collections.Immutable;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EventLoom.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainEventContractAnalyzer : DiagnosticAnalyzer
{
    public const string MissingEventTypeDiagnosticId = "EL0001";
    public const string MissingApplyHandlerDiagnosticId = "EL0002";
    public const string InvalidApplyHandlerDiagnosticId = "EL0003";
    public const string AmbiguousApplyHandlerDiagnosticId = "EL0004";
    public const string WrongAggregateOwnerDiagnosticId = "EL0005";

    private static readonly DiagnosticDescriptor MissingEventType = new(
        MissingEventTypeDiagnosticId,
        "Domain event requires a persisted identity",
        "Domain event '{0}' must declare EventTypeAttribute",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every persisted EventLoom domain event needs a stable EventTypeAttribute name and version.");

    private static readonly DiagnosticDescriptor MissingApplyHandler = new(
        MissingApplyHandlerDiagnosticId,
        "Aggregate event requires an Apply handler",
        "Aggregate '{0}' must declare an Apply({1}) handler",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every aggregate-owned event needs exactly one compatible Apply handler.");

    private static readonly DiagnosticDescriptor InvalidApplyHandler = new(
        InvalidApplyHandlerDiagnosticId,
        "Aggregate Apply handler has an invalid signature",
        "Apply handler '{0}' must be a non-static, non-generic, concrete, private or protected void method with one aggregate event parameter",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Apply handlers must match EventLoom's runtime dispatch contract.");

    private static readonly DiagnosticDescriptor AmbiguousApplyHandler = new(
        AmbiguousApplyHandlerDiagnosticId,
        "Aggregate event has multiple Apply handlers",
        "Aggregate '{0}' has multiple Apply({1}) handlers",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Runtime event dispatch requires exactly one Apply handler per event type.");

    private static readonly DiagnosticDescriptor WrongAggregateOwner = new(
        WrongAggregateOwnerDiagnosticId,
        "Aggregate cannot raise an event owned by another aggregate",
        "Aggregate '{0}' cannot raise event '{1}' owned by '{2}'",
        "EventLoom",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An aggregate may only raise events linked to itself or one of its base aggregate types.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            MissingEventType,
            MissingApplyHandler,
            InvalidApplyHandler,
            AmbiguousApplyHandler,
            WrongAggregateOwner);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var domainEvent = startContext.Compilation.GetTypeByMetadataName("EventLoom.IDomainEvent`1");
            var eventTypeAttribute = startContext.Compilation.GetTypeByMetadataName("EventLoom.EventTypeAttribute");
            var aggregate = startContext.Compilation.GetTypeByMetadataName("EventLoom.Aggregate`1");
            if (domainEvent is null || eventTypeAttribute is null || aggregate is null)
            {
                return;
            }

            startContext.RegisterSymbolAction(
                context => AnalyzeNamedType(context, domainEvent, eventTypeAttribute, aggregate),
                SymbolKind.NamedType);
            startContext.RegisterSymbolAction(
                context => AnalyzeMethod(context, domainEvent, aggregate),
                SymbolKind.Method);
            startContext.RegisterOperationAction(
                context => AnalyzeInvocation(context, domainEvent, aggregate),
                OperationKind.Invocation);
        });
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol domainEvent,
        INamedTypeSymbol eventTypeAttribute,
        INamedTypeSymbol aggregate)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsAbstract)
        {
            return;
        }

        var eventContract = GetEventContract(type, domainEvent);
        if (eventContract is null)
        {
            return;
        }

        var location = type.Locations.FirstOrDefault(static value => value.IsInSource);
        if (location is null)
        {
            return;
        }

        if (!type.GetAttributes().Any(attribute =>
                SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, eventTypeAttribute)))
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingEventType, location, type.Name));
        }

        var owner = eventContract.TypeArguments[0] as INamedTypeSymbol;
        if (owner is null || !InheritsFrom(owner, aggregate))
        {
            return;
        }

        var matchingHandlers = GetRuntimeVisibleMethods(owner)
            .Where(method =>
                method.Name == "Apply" &&
                method.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type))
            .ToArray();
        var validHandlers = matchingHandlers.Where(method => IsValidHandler(method, domainEvent)).ToArray();

        if (matchingHandlers.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingApplyHandler,
                location,
                owner.Name,
                type.Name));
        }
        else if (validHandlers.Length > 1)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                AmbiguousApplyHandler,
                location,
                owner.Name,
                type.Name));
        }
    }

    private static void AnalyzeMethod(
        SymbolAnalysisContext context,
        INamedTypeSymbol domainEvent,
        INamedTypeSymbol aggregate)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.Name != "Apply" ||
            method.MethodKind != MethodKind.Ordinary ||
            method.ContainingType is null ||
            !InheritsFrom(method.ContainingType, aggregate) ||
            IsValidHandler(method, domainEvent))
        {
            return;
        }

        var location = method.Locations.FirstOrDefault(static value => value.IsInSource);
        if (location is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidApplyHandler,
                location,
                method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
        }
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol domainEvent,
        INamedTypeSymbol aggregate)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (invocation.TargetMethod.Name != "Raise" ||
            invocation.Arguments.Length != 1 ||
            invocation.TargetMethod.ContainingType is not { } containingType ||
            !SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, aggregate) ||
            invocation.Arguments[0].Value.Type is not INamedTypeSymbol eventType ||
            GetEventContract(eventType, domainEvent) is not { } eventContract ||
            eventContract.TypeArguments[0] is not INamedTypeSymbol owner)
        {
            return;
        }

        var enclosingType = context.ContainingSymbol?.ContainingType;
        if (enclosingType is null || InheritsFromOrEquals(enclosingType, owner))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            WrongAggregateOwner,
            invocation.Syntax.GetLocation(),
            enclosingType.Name,
            eventType.Name,
            owner.Name));
    }

    private static INamedTypeSymbol? GetEventContract(
        INamedTypeSymbol type,
        INamedTypeSymbol domainEvent) =>
        type.AllInterfaces.FirstOrDefault(candidate =>
            SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, domainEvent));

    private static bool IsValidHandler(IMethodSymbol method, INamedTypeSymbol domainEvent) =>
        !method.IsStatic &&
        !method.IsAbstract &&
        !method.IsGenericMethod &&
        method.Parameters.Length == 1 &&
        method.ReturnsVoid &&
        method.DeclaredAccessibility is Accessibility.Private or
            Accessibility.Protected or
            Accessibility.ProtectedAndInternal or
            Accessibility.ProtectedOrInternal &&
        method.Parameters[0].Type is INamedTypeSymbol eventType &&
        GetEventContract(eventType, domainEvent) is not null;

    private static IEnumerable<IMethodSymbol> GetRuntimeVisibleMethods(INamedTypeSymbol type)
    {
        var current = type;
        var isOwner = true;
        while (current is not null)
        {
            foreach (var method in current.GetMembers("Apply").OfType<IMethodSymbol>())
            {
                if (isOwner || method.DeclaredAccessibility != Accessibility.Private)
                {
                    yield return method;
                }
            }

            isOwner = false;
            current = current.BaseType;
        }
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol openBase)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, openBase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool InheritsFromOrEquals(INamedTypeSymbol type, INamedTypeSymbol expectedBase)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, expectedBase))
            {
                return true;
            }
        }

        return false;
    }
}
