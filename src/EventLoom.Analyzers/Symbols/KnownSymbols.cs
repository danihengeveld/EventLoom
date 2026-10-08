using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace EventLoom.Analyzers.Symbols;

/// <summary>Resolves EventLoom and BCL symbols once per compilation and answers questions about them.</summary>
internal sealed class KnownSymbols
{
    private static readonly string[] NondeterministicTypes =
    [
        "System.Random",
        "System.TimeProvider",
        "System.Environment",
        "System.Diagnostics.Stopwatch",
        "System.Security.Cryptography.RandomNumberGenerator",
        "System.IO.File",
        "System.IO.Directory",
        "System.Net.Http.HttpClient",
        "System.Threading.Tasks.Task"
    ];

    private static readonly (string Type, string Member)[] NondeterministicMembers =
    [
        ("System.DateTime", "Now"),
        ("System.DateTime", "UtcNow"),
        ("System.DateTime", "Today"),
        ("System.DateTimeOffset", "Now"),
        ("System.DateTimeOffset", "UtcNow"),
        ("System.Guid", "NewGuid"),
        ("System.Guid", "CreateVersion7")
    ];

    private readonly ImmutableHashSet<INamedTypeSymbol> nondeterministicTypes;
    private readonly ImmutableArray<(INamedTypeSymbol Type, string Member)> nondeterministicMembers;

    private KnownSymbols(
        INamedTypeSymbol aggregate,
        INamedTypeSymbol apply,
        INamedTypeSymbol domainEventOwner,
        INamedTypeSymbol domainEvent,
        INamedTypeSymbol snapshot,
        INamedTypeSymbol snapshotable,
        ImmutableHashSet<INamedTypeSymbol> nondeterministicTypes,
        ImmutableArray<(INamedTypeSymbol Type, string Member)> nondeterministicMembers)
    {
        Aggregate = aggregate;
        Apply = apply;
        DomainEventOwner = domainEventOwner;
        DomainEvent = domainEvent;
        Snapshot = snapshot;
        Snapshotable = snapshotable;
        this.nondeterministicTypes = nondeterministicTypes;
        this.nondeterministicMembers = nondeterministicMembers;
    }

    public INamedTypeSymbol Aggregate { get; }

    public INamedTypeSymbol Apply { get; }

    public INamedTypeSymbol DomainEventOwner { get; }

    public INamedTypeSymbol DomainEvent { get; }

    public INamedTypeSymbol Snapshot { get; }

    public INamedTypeSymbol Snapshotable { get; }

    public static KnownSymbols? Create(Compilation compilation)
    {
        var aggregate = compilation.GetTypeByMetadataName("EventLoom.Aggregate`2");
        var apply = compilation.GetTypeByMetadataName("EventLoom.IApply`1");
        var owner = compilation.GetTypeByMetadataName("EventLoom.IDomainEvent`1");
        var domainEvent = compilation.GetTypeByMetadataName("EventLoom.IDomainEvent`2");
        var snapshot = compilation.GetTypeByMetadataName("EventLoom.IAggregateSnapshot`2");
        var snapshotable = compilation.GetTypeByMetadataName("EventLoom.ISnapshotable`1");
        if (aggregate is null || apply is null || owner is null || domainEvent is null ||
            snapshot is null || snapshotable is null)
        {
            return null;
        }

        var types = NondeterministicTypes
            .Select(compilation.GetTypeByMetadataName)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .ToImmutableHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var members = NondeterministicMembers
            .Select(value => (Type: compilation.GetTypeByMetadataName(value.Type), value.Member))
            .Where(static value => value.Type is not null)
            .Select(static value => (value.Type!, value.Member))
            .ToImmutableArray();
        return new KnownSymbols(aggregate, apply, owner, domainEvent, snapshot, snapshotable, types, members);
    }

    public INamedTypeSymbol? FindAggregateBase(INamedTypeSymbol? type)
    {
        for (var current = type?.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, Aggregate))
            {
                return current;
            }
        }

        return null;
    }

    public IEnumerable<IdentityContract> GetIdentityContracts(INamedTypeSymbol type)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            yield break;
        }

        foreach (var contract in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, DomainEvent))
            {
                yield return new IdentityContract("Event", contract, "EventType", "EventVersion");
            }
            else if (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, Snapshot))
            {
                yield return new IdentityContract("Snapshot", contract, "SnapshotType", "SnapshotVersion");
            }
        }
    }

    public bool TryGetIdentityRole(IPropertySymbol property, out IdentityContract contract, out IdentityRole role)
    {
        if (property.IsStatic)
        {
            foreach (var candidate in GetIdentityContracts(property.ContainingType))
            {
                foreach (var candidateRole in candidate.Roles)
                {
                    if (SymbolEqualityComparer.Default.Equals(
                            property.ContainingType.FindImplementationForInterfaceMember(candidateRole.Member),
                            property))
                    {
                        contract = candidate;
                        role = candidateRole;
                        return true;
                    }
                }
            }
        }

        contract = null!;
        role = null!;
        return false;
    }

    public bool IsHandlerMember(IMethodSymbol method) =>
        method.ContainingType is { } type &&
        (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, Apply) ||
         SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, Snapshotable));

    public bool IsHandlerMethod(IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Any(IsHandlerMember))
        {
            return true;
        }

        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic ||
            method.Name is not ("Apply" or "CreateSnapshot" or "RestoreSnapshot"))
        {
            return false;
        }

        foreach (var contract in method.ContainingType.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, Apply) &&
                !SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, Snapshotable))
            {
                continue;
            }

            foreach (var member in contract.GetMembers(method.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(
                        method.ContainingType.FindImplementationForInterfaceMember(member),
                        method))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool IsNondeterministic(ISymbol member)
    {
        var type = member.ContainingType;
        if (type is null)
        {
            return false;
        }

        if (nondeterministicTypes.Contains(type.OriginalDefinition))
        {
            return true;
        }

        foreach (var candidate in nondeterministicMembers)
        {
            if (candidate.Member == member.Name &&
                SymbolEqualityComparer.Default.Equals(candidate.Type, type))
            {
                return true;
            }
        }

        return false;
    }
}
