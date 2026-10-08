using Microsoft.CodeAnalysis;

namespace EventLoom.Analyzers.Symbols;

/// <summary>A persisted identity name declared by a type, collected for duplicate detection.</summary>
internal readonly struct DeclaredIdentity(string kind, string name, INamedTypeSymbol type, Location location)
{
    public string Kind { get; } = kind;

    public string Name { get; } = name;

    public INamedTypeSymbol Type { get; } = type;

    public Location Location { get; } = location;
}
