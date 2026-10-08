using System.Linq;
using EventLoom.Analyzers.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EventLoom.Analyzers.Checks;

/// <summary>Checks that apply to event and snapshot contract types.</summary>
internal static class ContractChecks
{
    internal static void AnalyzeMutability(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            var mutable = member switch
            {
                IPropertySymbol property => !property.IsStatic &&
                                            property.SetMethod is { IsInitOnly: false },
                IFieldSymbol field => !field.IsStatic &&
                                      !field.IsReadOnly &&
                                      !field.IsConst &&
                                      !field.IsImplicitlyDeclared &&
                                      field.AssociatedSymbol is null,
                _ => false
            };
            if (!mutable)
            {
                continue;
            }

            var location = member.Locations.FirstOrDefault(static value => value.IsInSource) ??
                           type.Locations.First(static value => value.IsInSource);
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.MutableContract, location, type.Name, member.Name));
        }
    }

    internal static void AnalyzeAutoPropertyIdentity(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        IdentityContract contract)
    {
        foreach (var role in contract.Roles)
        {
            if (type.FindImplementationForInterfaceMember(role.Member) is not IPropertySymbol implementation ||
                !SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type))
            {
                continue;
            }

            foreach (var reference in implementation.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(context.CancellationToken) is PropertyDeclarationSyntax
                    {
                        ExpressionBody: null, Initializer: null, AccessorList: { } accessors
                    } declaration &&
                    accessors.Accessors.All(static accessor =>
                        accessor.Body is null && accessor.ExpressionBody is null))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.NonConstantIdentity,
                        declaration.Identifier.GetLocation(),
                        type.Name,
                        role.Member.Name,
                        role.Expectation));
                }
            }
        }
    }
}
