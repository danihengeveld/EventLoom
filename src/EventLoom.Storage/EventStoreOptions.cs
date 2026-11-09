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

/// <summary>Controls whether EventLoom uses one configured tenant or resolves tenants per operation.</summary>
internal enum TenancyMode
{
    /// <summary>Uses one configured tenant for normal application operations.</summary>
    SingleTenant,

    /// <summary>Requires a scoped tenant accessor and validates explicit tenant operations against it.</summary>
    MultiTenant
}

/// <summary>Dispatches explicitly registered inline projections during an event append.</summary>
internal interface IInlineProjectionDispatcher
{
    /// <summary>Dispatches an event to its inline projection handlers.</summary>
    /// <param name="envelope">The persisted event envelope.</param>
    /// <param name="cancellationToken">Cancels projection execution.</param>
    Task DispatchAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}
