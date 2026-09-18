namespace EventLoom;

/// <summary>Represents a normalized tenant identifier.</summary>
public readonly record struct TenantId
{
    public TenantId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Any(char.IsControl) || normalized.Length > 256)
        {
            throw new ArgumentException("Tenant ID contains invalid characters or is too long.", nameof(value));
        }

        Value = normalized;
    }

    /// <summary>Gets the normalized tenant value.</summary>
    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Resolves the tenant associated with the current scoped operation.</summary>
public interface ITenantAccessor
{
    TenantId? TenantId { get; }
}

/// <summary>Controls whether EventLoom uses one configured tenant or resolves tenants per operation.</summary>
public enum TenancyMode
{
    /// <summary>Uses one configured tenant for normal application operations.</summary>
    SingleTenant,

    /// <summary>Requires a scoped tenant accessor and validates explicit tenant operations against it.</summary>
    MultiTenant
}
