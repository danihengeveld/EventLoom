using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace EventLoom.Analyzers.Symbols;

/// <summary>An event or snapshot contract and the identity members it requires.</summary>
internal sealed class IdentityContract
{
    public IdentityContract(string kind, INamedTypeSymbol contract, string nameMember, string versionMember)
    {
        Kind = kind;
        Roles = contract.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(value => value.Name == nameMember || value.Name == versionMember)
            .Select(value => new IdentityRole(
                value,
                value.Name == nameMember,
                value.Name == nameMember ? "a non-empty constant string" : "a positive constant integer"))
            .ToImmutableArray();
    }

    public string Kind { get; }

    public ImmutableArray<IdentityRole> Roles { get; }
}

/// <summary>One persisted identity member of a contract, such as the event name or version.</summary>
internal sealed class IdentityRole(IPropertySymbol member, bool isName, string expectation)
{
    public IPropertySymbol Member { get; } = member;

    public bool IsName { get; } = isName;

    public string Expectation { get; } = expectation;
}
