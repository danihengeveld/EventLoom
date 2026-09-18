using EventLoom.Hosting;

namespace EventLoom.Testing;

/// <summary>Configures an <see cref="EventLoomSqliteTestHost"/>.</summary>
public sealed class EventLoomTestHostOptions
{
    /// <summary>
    /// Gets or sets the single tenant identifier used by the host. The default is <c>"default"</c>.
    /// </summary>
    public string TenantId { get; set; } = "default";

    /// <summary>
    /// Gets or sets the initial current time exposed through <see cref="EventLoomSqliteTestHost.Clock"/>.
    /// The default is <c>2000-01-01T00:00:00Z</c>.
    /// </summary>
    public DateTimeOffset StartTime { get; set; } = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Gets or sets a value indicating whether persisted event identifiers are generated
    /// deterministically with <see cref="SequentialEventIdGenerator"/> instead of
    /// <see cref="UuidV7EventIdGenerator"/>. The default is <see langword="true"/>.
    /// </summary>
    public bool UseDeterministicEventIds { get; set; } = true;

    /// <summary>
    /// Gets or sets the callback used to register events, aggregates, and projections.
    /// The host applies <c>UseSqlite</c>, tenancy, and time-provider configuration itself;
    /// this callback must not call those extensions.
    /// </summary>
    public Action<EventLoomBuilder>? ConfigureEventLoom { get; set; }
}
