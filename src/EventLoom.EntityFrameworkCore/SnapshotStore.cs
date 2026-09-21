using System.Data;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Persists and retrieves versioned aggregate snapshots independently of immutable event history.</summary>
internal sealed class SnapshotStore(
    EventStoreDbContext context,
    TimeProvider timeProvider,
    ISnapshotRetentionPolicy? retentionPolicy = null)
{
    private readonly EventStoreDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ISnapshotRetentionPolicy retentionPolicy = retentionPolicy ?? new KeepLatestSnapshotsPolicy(1);

    /// <summary>Writes a snapshot and applies the configured retention policy for the aggregate stream.</summary>
    public Task WriteAsync(
        SnapshotWriteRequest request,
        CancellationToken cancellationToken = default) =>
        WriteWithRetentionAsync(request, null, cancellationToken);

    /// <summary>Writes a snapshot using an optional aggregate-specific retention policy.</summary>
    public async Task WriteWithRetentionAsync(
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

        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var snapshot = new SnapshotEntity
        {
            TenantId = tenantId,
            StreamId = request.StreamId,
            AggregateType = request.AggregateType,
            StreamVersion = request.StreamVersion,
            SnapshotType = request.SnapshotType,
            SchemaVersion = request.SchemaVersion,
            Payload = request.Payload,
            CreatedAt = timeProvider.GetUtcNow()
        };
        context.Snapshots.Add(snapshot);
        await context.SaveChangesAsync(cancellationToken);
        var expiredSnapshotIds = await context.Snapshots
            .Where(value =>
                value.TenantId == tenantId &&
                value.StreamId == request.StreamId &&
                value.AggregateType == request.AggregateType)
            .OrderByDescending(value => value.StreamVersion)
            .ThenByDescending(value => value.Id)
            .Skip((retentionPolicy ?? this.retentionPolicy).SnapshotsToRetain)
            .Select(value => value.Id)
            .ToArrayAsync(cancellationToken);
        if (expiredSnapshotIds.Length > 0)
        {
            await context.Snapshots
                .Where(value => expiredSnapshotIds.Contains(value.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Gets the latest persisted snapshot for an aggregate stream and configured snapshot type.</summary>
    public async Task<SnapshotEnvelope?> ReadLatestAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        string snapshotType,
        CancellationToken cancellationToken = default)
    {
        tenantId = new TenantId(tenantId).Value;
        var snapshot = await context.Snapshots.AsNoTracking()
            .Where(value =>
                value.TenantId == tenantId &&
                value.StreamId == streamId &&
                value.AggregateType == aggregateType &&
                value.SnapshotType == snapshotType)
            .OrderByDescending(value => value.StreamVersion)
            .FirstOrDefaultAsync(cancellationToken);
        return snapshot is null
            ? null
            : new SnapshotEnvelope(
                snapshot.TenantId,
                snapshot.StreamId,
                snapshot.AggregateType,
                snapshot.StreamVersion,
                snapshot.SnapshotType,
                snapshot.SchemaVersion,
                snapshot.Payload,
                snapshot.CreatedAt);
    }

    /// <summary>Removes a specific unusable snapshot without modifying the aggregate's event history.</summary>
    /// <param name="snapshot">The snapshot to remove.</param>
    /// <param name="cancellationToken">Cancels the database operation.</param>
    public async Task InvalidateAsync(SnapshotEnvelope snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await context.Snapshots
            .Where(value =>
                value.TenantId == snapshot.TenantId &&
                value.StreamId == snapshot.StreamId &&
                value.AggregateType == snapshot.AggregateType &&
                value.StreamVersion == snapshot.StreamVersion &&
                value.SnapshotType == snapshot.SnapshotType &&
                value.SchemaVersion == snapshot.SchemaVersion &&
                value.Payload == snapshot.Payload &&
                value.CreatedAt == snapshot.CreatedAt)
            .ExecuteDeleteAsync(cancellationToken);
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

/// <summary>Describes a persisted aggregate snapshot.</summary>
internal sealed record SnapshotEnvelope(
    string TenantId,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    string SnapshotType,
    int SchemaVersion,
    string Payload,
    DateTimeOffset CreatedAt);
