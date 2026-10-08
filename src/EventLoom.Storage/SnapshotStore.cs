namespace EventLoom.Storage;

/// <summary>Validates snapshot requests, applies retention, and delegates persistence to a provider.</summary>
internal sealed class SnapshotStore(
    ISnapshotStorage storage,
    TimeProvider timeProvider,
    ISnapshotRetentionPolicy? retentionPolicy = null)
{
    private readonly ISnapshotStorage storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ISnapshotRetentionPolicy retentionPolicy = retentionPolicy ?? new KeepLatestSnapshotsPolicy(1);

    /// <summary>Writes a snapshot and applies the configured retention policy for the aggregate stream.</summary>
    public Task WriteAsync(
        SnapshotWriteRequest request,
        CancellationToken cancellationToken = default) =>
        WriteWithRetentionAsync(request, null, cancellationToken);

    /// <summary>Writes a snapshot using an optional aggregate-specific retention policy.</summary>
    public Task WriteWithRetentionAsync(
        SnapshotWriteRequest request,
        ISnapshotRetentionPolicy? retentionPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = new TenantId(request.TenantId).Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StreamId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AggregateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SnapshotType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Payload);
        if (request.StreamVersion < 1 || request.SchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        return storage.WriteAsync(
            new SnapshotRecord(
                tenantId,
                request.StreamId,
                request.AggregateType,
                request.StreamVersion,
                request.SnapshotType,
                request.SchemaVersion,
                request.Payload,
                timeProvider.GetUtcNow()),
            (retentionPolicy ?? this.retentionPolicy).SnapshotsToRetain,
            cancellationToken);
    }

    /// <summary>Gets the latest persisted snapshot for an aggregate stream and configured snapshot type.</summary>
    public Task<SnapshotRecord?> ReadLatestAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        string snapshotType,
        CancellationToken cancellationToken = default) =>
        storage.ReadLatestAsync(new TenantId(tenantId).Value, streamId, aggregateType, snapshotType, cancellationToken);

    /// <summary>Removes a specific unusable snapshot without modifying the aggregate's event history.</summary>
    public Task InvalidateAsync(SnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return storage.InvalidateAsync(snapshot, cancellationToken);
    }
}

/// <summary>Describes a snapshot write request.</summary>
internal sealed record SnapshotWriteRequest(
    string TenantId,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    string SnapshotType,
    int SchemaVersion,
    string Payload);
