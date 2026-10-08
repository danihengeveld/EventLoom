namespace EventLoom.Storage;

/// <summary>Carries the provider-independent settings the event store engine needs.</summary>
internal sealed class EventStoreOptions
{
    /// <summary>Gets or sets whether appends also write outbox messages.</summary>
    public bool OutboxEnabled { get; set; }

    /// <summary>Gets or sets the tenancy mode.</summary>
    public TenancyMode TenancyMode { get; set; } = TenancyMode.SingleTenant;

    /// <summary>Gets or sets the tenant identifier used in single-tenant mode.</summary>
    public string SingleTenantId { get; set; } = "default";
}
