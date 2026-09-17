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

/// <summary>Controls whether tenant context is disabled or required.</summary>
public enum TenancyMode
{
    Disabled,
    Required
}
