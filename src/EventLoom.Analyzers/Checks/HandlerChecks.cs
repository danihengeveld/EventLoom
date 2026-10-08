using System.Linq;
using EventLoom.Analyzers.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EventLoom.Analyzers.Checks;

/// <summary>Checks for aggregate command methods and Apply or snapshot callbacks.</summary>
internal static class HandlerChecks
{
    internal static void AnalyzeMethod(SymbolAnalysisContext context, KnownSymbols symbols)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.IsAsync && symbols.IsHandlerMethod(method))
        {
            var location = method.Locations.FirstOrDefault(static value => value.IsInSource);
            if (location is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.NondeterministicHandler,
                    location,
                    method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                    "async"));
            }
        }
    }

    internal static void AnalyzeInvocation(OperationAnalysisContext context, KnownSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var target = invocation.TargetMethod;
        var containing = context.ContainingSymbol as IMethodSymbol;

        if (target.Name == "Raise" &&
            SymbolEqualityComparer.Default.Equals(target.ContainingType?.OriginalDefinition, symbols.Aggregate) &&
            containing is not null)
        {
            if (containing.MethodKind == MethodKind.Constructor)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.RaiseInConstructor,
                    invocation.Syntax.GetLocation(),
                    containing.ContainingType.Name));
            }
            else if (symbols.IsHandlerMethod(containing))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.RaiseInHandler,
                    invocation.Syntax.GetLocation(),
                    containing.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
            }
        }

        if (symbols.IsHandlerMember(target) || symbols.IsHandlerMethod(target))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.DirectHandlerCall,
                invocation.Syntax.GetLocation(),
                target.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
        }

        ReportNondeterminism(context, symbols, target);
    }

    internal static void AnalyzeMemberUse(OperationAnalysisContext context, KnownSymbols symbols)
    {
        ISymbol? member = context.Operation switch
        {
            IPropertyReferenceOperation value => value.Property,
            IFieldReferenceOperation value => value.Field,
            IMethodReferenceOperation value => value.Method,
            IObjectCreationOperation value => value.Constructor,
            _ => null
        };
        if (member is not null)
        {
            ReportNondeterminism(context, symbols, member);
        }
    }

    private static void ReportNondeterminism(OperationAnalysisContext context, KnownSymbols symbols, ISymbol member)
    {
        if (context.ContainingSymbol is not IMethodSymbol containing ||
            !symbols.IsHandlerMethod(containing) ||
            !symbols.IsNondeterministic(member))
        {
            return;
        }

        var name = member is IMethodSymbol { MethodKind: MethodKind.Constructor }
            ? member.ContainingType.Name
            : $"{member.ContainingType.Name}.{member.Name}";
        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.NondeterministicHandler,
            context.Operation.Syntax.GetLocation(),
            containing.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
            name));
    }

    internal static void AnalyzeAssignment(OperationAnalysisContext context, KnownSymbols symbols)
    {
        var target = context.Operation switch
        {
            IAssignmentOperation value => value.Target,
            IIncrementOrDecrementOperation value => value.Target,
            _ => null
        };
        ISymbol? member = target switch
        {
            IFieldReferenceOperation { Field.IsStatic: false, Instance: IInstanceReferenceOperation } value =>
                value.Field,
            IPropertyReferenceOperation { Property.IsStatic: false, Instance: IInstanceReferenceOperation } value =>
                value.Property,
            _ => null
        };
        if (member is null ||
            context.ContainingSymbol is not IMethodSymbol containing ||
            containing.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ||
            symbols.FindAggregateBase(containing.ContainingType) is null ||
            symbols.IsHandlerMethod(containing))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.StateChangedOutsideApply,
            context.Operation.Syntax.GetLocation(),
            containing.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
            member.Name));
    }
}
