namespace EventLoom.EntityFrameworkCore;

using EventLoom;

/// <summary>
/// Controls the names and schema used by the EventLoom event-store tables.
/// </summary>
public sealed class EventStoreOptions
{
    internal bool OutboxEnabled { get; set; }

    /// <summary>
    /// Gets or sets the tenancy mode. The default is <see cref="TenancyMode.SingleTenant"/>.
    /// </summary>
    public TenancyMode TenancyMode { get; set; } = TenancyMode.SingleTenant;

    /// <summary>
    /// Gets or sets the tenant identifier used in <see cref="TenancyMode.SingleTenant"/> mode.
    /// </summary>
    public string SingleTenantId { get; set; } = "default";

    /// <summary>
    /// Gets or sets the database schema name. The default is <c>eventloom</c>.
    /// </summary>
    public string Schema { get; set; } = "eventloom";

    /// <summary>
    /// Gets or sets the prefix applied to event-store table names. The default is <c>eventloom_</c>.
    /// </summary>
    public string TablePrefix { get; set; } = "eventloom_";

    /// <summary>
    /// Gets or sets a value indicating whether the configured schema is used.
    /// Set this to <see langword="false"/> for providers such as SQLite.
    /// </summary>
    public bool UseSchema { get; set; }
}
