using System.Collections.Concurrent;
using System.Linq;
using EventLoom.Analyzers.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EventLoom.Analyzers.Checks;

/// <summary>Checks for persisted event and snapshot names and versions.</summary>
internal static class IdentityChecks
{
    internal static void AnalyzeIdentityGetter(
        OperationBlockAnalysisContext context,
        KnownSymbols symbols,
        ConcurrentBag<DeclaredIdentity> identities)
    {
        if (context.OwningSymbol is not IMethodSymbol { MethodKind: MethodKind.PropertyGet } getter ||
            getter.AssociatedSymbol is not IPropertySymbol property ||
            !symbols.TryGetIdentityRole(property, out var contract, out var role))
        {
            return;
        }

        foreach (var block in context.OperationBlocks)
        {
            foreach (var returned in block.DescendantsAndSelf().OfType<IReturnOperation>())
            {
                ValidateIdentityValue(
                    context.ReportDiagnostic,
                    identities,
                    property,
                    contract,
                    role,
                    returned.ReturnedValue,
                    returned.Syntax.GetLocation());
            }
        }
    }

    internal static void AnalyzeIdentityInitializer(
        OperationAnalysisContext context,
        KnownSymbols symbols,
        ConcurrentBag<DeclaredIdentity> identities)
    {
        var initializer = (IPropertyInitializerOperation)context.Operation;
        foreach (var property in initializer.InitializedProperties)
        {
            if (symbols.TryGetIdentityRole(property, out var contract, out var role))
            {
                ValidateIdentityValue(
                    context.ReportDiagnostic,
                    identities,
                    property,
                    contract,
                    role,
                    initializer.Value,
                    initializer.Syntax.GetLocation());
            }
        }
    }

    private static void ValidateIdentityValue(
        System.Action<Diagnostic> report,
        ConcurrentBag<DeclaredIdentity> identities,
        IPropertySymbol property,
        IdentityContract contract,
        IdentityRole role,
        IOperation? value,
        Location location)
    {
        var constant = value?.ConstantValue ?? default;
        var valid = constant.HasValue && (role.IsName
            ? constant.Value is string name && !string.IsNullOrWhiteSpace(name)
            : constant.Value is int version && version > 0);
        if (!valid)
        {
            report(Diagnostic.Create(
                Descriptors.NonConstantIdentity,
                location,
                property.ContainingType.Name,
                role.Member.Name,
                role.Expectation));
            return;
        }

        if (role.IsName)
        {
            identities.Add(new DeclaredIdentity(
                contract.Kind,
                (string)constant.Value!,
                property.ContainingType,
                property.ContainingType.Locations.FirstOrDefault(static item => item.IsInSource) ?? location));
        }
    }

    internal static void ReportDuplicateIdentities(
        CompilationAnalysisContext context,
        ConcurrentBag<DeclaredIdentity> identities)
    {
        foreach (var group in identities.GroupBy(static value => (value.Kind, value.Name)))
        {
            var types = group
                .GroupBy(static value => value.Type, SymbolEqualityComparer.Default)
                .Select(static value => value.First())
                .OrderBy(static value => value.Type.ToDisplayString(), System.StringComparer.Ordinal)
                .ToArray();
            if (types.Length < 2)
            {
                continue;
            }

            var names = string.Join(
                ", ",
                types.Select(static value => value.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            foreach (var duplicate in types)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.DuplicateIdentity,
                    duplicate.Location,
                    group.Key.Kind,
                    group.Key.Name,
                    names));
            }
        }
    }
}
