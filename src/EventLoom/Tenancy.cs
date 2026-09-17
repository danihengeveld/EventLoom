namespace EventLoom;

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

    public string Value { get; }

    public override string ToString() => Value;
}

public interface ITenantAccessor
{
    TenantId? TenantId { get; }
}

public enum TenancyMode
{
    Disabled,
    Required
}
