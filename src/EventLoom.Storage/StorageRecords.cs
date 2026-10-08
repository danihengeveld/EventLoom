namespace EventLoom.Storage;

/// <summary>Describes an event that EventLoom asks a storage provider to persist.</summary>
/// <param name="EventId">The unique event identifier assigned by EventLoom.</param>
/// <param name="EventType">The stable persisted event name.</param>
/// <param name="EventTypeVersion">The persisted event schema version.</param>
/// <param name="Payload">The serialized event payload.</param>
/// <param name="OccurredAt">The UTC timestamp assigned by EventLoom.</param>
public sealed record NewStoredEvent(
    Guid EventId,
    string EventType,
    int EventTypeVersion,
    string Payload,
    DateTimeOffset OccurredAt);

/// <summary>Describes a committed event exactly as a storage provider persisted it.</summary>
public sealed record StoredEvent(
    Guid EventId,
    string TenantId,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    long TenantOffset,
    string EventType,
    int EventTypeVersion,
    string Payload,
    DateTimeOffset OccurredAt,
    EventMetadata Metadata);

/// <summary>Describes one atomic append that a storage provider must persist.</summary>
/// <param name="TenantId">The normalized tenant that owns the stream.</param>
/// <param name="StreamId">The stream receiving the events.</param>
/// <param name="AggregateType">The aggregate type recorded when the stream is created.</param>
/// <param name="ExpectedVersion">The stream version expectation to enforce.</param>
/// <param name="Events">The events to append in order. The list is never empty.</param>
/// <param name="Metadata">Operational metadata shared by every event in the batch.</param>
/// <param name="AppendId">An optional idempotency key unique within the tenant.</param>
/// <param name="WriteOutbox">Whether each event must also be written to the outbox in the same transaction.</param>
/// <param name="BeforeCommit">
/// An optional callback the provider invokes after it has assigned stream versions and tenant offsets and
/// staged its writes, but before it commits. Inline projections run here. A failure aborts the append.
/// </param>
public sealed record StorageAppendRequest(
    string TenantId,
    string StreamId,
    string AggregateType,
    ExpectedVersion ExpectedVersion,
    IReadOnlyList<NewStoredEvent> Events,
    EventMetadata Metadata,
    string? AppendId,
    bool WriteOutbox,
    Func<IReadOnlyList<StoredEvent>, CancellationToken, Task>? BeforeCommit = null);

/// <summary>Describes the outcome of a provider-level append.</summary>
/// <param name="Events">The committed events, or the events of the original append for an idempotent replay.</param>
/// <param name="WasIdempotentReplay">Whether the <c>AppendId</c> matched an earlier append, so nothing was written.</param>
public sealed record StorageAppendResult(IReadOnlyList<StoredEvent> Events, bool WasIdempotentReplay);

/// <summary>Describes the highest committed tenant offset for one tenant.</summary>
public sealed record TenantHead(string TenantId, long LastOffset);

/// <summary>Describes a persisted aggregate snapshot.</summary>
public sealed record SnapshotRecord(
    string TenantId,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    string SnapshotType,
    int SchemaVersion,
    string Payload,
    DateTimeOffset CreatedAt);

/// <summary>Describes an acquired tenant-scoped worker lease and its fencing token.</summary>
public sealed record WorkerLease(
    string TenantId,
    string LeaseName,
    string OwnerId,
    long FencingToken,
    DateTimeOffset LeaseUntil);

/// <summary>Reports whether a storage provider can reach and use its configured store.</summary>
/// <param name="CanConnect">Whether the store was reachable.</param>
/// <param name="MissingCount">The number of expected storage objects that do not exist.</param>
/// <param name="IncompatibleCount">The number of storage objects whose shape is incompatible.</param>
public sealed record StorageValidationResult(bool CanConnect, int MissingCount, int IncompatibleCount)
{
    /// <summary>Gets whether the store is reachable and every expected storage object is present and compatible.</summary>
    public bool IsCompatible => CanConnect && MissingCount == 0 && IncompatibleCount == 0;
}

/// <summary>Describes what a storage provider guarantees beyond the mandatory EventLoom contract.</summary>
/// <remarks>
/// Every provider must make appends atomic, enforce optimistic concurrency, and assign gapless, monotonic
/// tenant offsets that become visible in offset order. Capabilities describe the optional extras.
/// </remarks>
/// <param name="ProviderName">A short stable provider name used in diagnostics and error messages.</param>
/// <param name="SupportsTransactionalProjections">
/// Whether a projection's read-model writes and checkpoint advance can commit atomically.
/// </param>
/// <param name="SupportsInlineProjections">Whether inline projections can run inside the append transaction.</param>
/// <param name="SupportsUnitOfWork">Whether callers can share one transaction with an append.</param>
/// <param name="IsDistributed">Whether several application instances can safely share the store.</param>
public sealed record StorageCapabilities(
    string ProviderName,
    bool SupportsTransactionalProjections,
    bool SupportsInlineProjections,
    bool SupportsUnitOfWork,
    bool IsDistributed);
