using Microsoft.EntityFrameworkCore;
using EventLoom;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Persists and retrieves versioned aggregate snapshots independently of immutable event history.</summary>
public sealed class SnapshotStore(EventStoreDbContext context, TimeProvider timeProvider)
{
    private readonly EventStoreDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Writes a snapshot and retains only the latest snapshot for the aggregate stream.</summary>
    public async Task WriteAsync(SnapshotWriteRequest request, CancellationToken cancellationToken = default)
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

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
        await context.Snapshots
            .Where(value =>
                value.TenantId == tenantId &&
                value.StreamId == request.StreamId &&
                value.AggregateType == request.AggregateType &&
                value.Id != snapshot.Id)
            .ExecuteDeleteAsync(cancellationToken);
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
        return snapshot is null ? null : new SnapshotEnvelope(
            snapshot.TenantId,
            snapshot.StreamId,
            snapshot.AggregateType,
            snapshot.StreamVersion,
            snapshot.SnapshotType,
            snapshot.SchemaVersion,
            snapshot.Payload,
            snapshot.CreatedAt);
    }
}

/// <summary>Describes a snapshot write request.</summary>
public sealed record SnapshotWriteRequest(
    string TenantId,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    string SnapshotType,
    int SchemaVersion,
    string Payload);

/// <summary>Describes a persisted aggregate snapshot.</summary>
public sealed record SnapshotEnvelope(
    string TenantId,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    string SnapshotType,
    int SchemaVersion,
    string Payload,
    DateTimeOffset CreatedAt);
