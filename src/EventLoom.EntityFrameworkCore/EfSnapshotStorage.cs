using System.Data;
using EventLoom.Storage;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Persists aggregate snapshots through an EF Core context.</summary>
internal sealed class EfSnapshotStorage(EventStoreDbContext context) : ISnapshotStorage
{
    public async Task WriteAsync(
        SnapshotRecord snapshot,
        int snapshotsToRetain,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        context.Snapshots.Add(new SnapshotEntity
        {
            TenantId = snapshot.TenantId,
            StreamId = snapshot.StreamId,
            AggregateType = snapshot.AggregateType,
            StreamVersion = snapshot.StreamVersion,
            SnapshotType = snapshot.SnapshotType,
            SchemaVersion = snapshot.SchemaVersion,
            Payload = snapshot.Payload,
            CreatedAt = snapshot.CreatedAt
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var expiredSnapshotIds = await context.Snapshots
            .Where(value =>
                value.TenantId == snapshot.TenantId &&
                value.StreamId == snapshot.StreamId &&
                value.AggregateType == snapshot.AggregateType)
            .OrderByDescending(value => value.StreamVersion)
            .ThenByDescending(value => value.Id)
            .Skip(snapshotsToRetain)
            .Select(value => value.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (expiredSnapshotIds.Length > 0)
        {
            await context.Snapshots
                .Where(value => expiredSnapshotIds.Contains(value.Id))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SnapshotRecord?> ReadLatestAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        string snapshotType,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await context.Snapshots.AsNoTracking()
            .Where(value =>
                value.TenantId == tenantId &&
                value.StreamId == streamId &&
                value.AggregateType == aggregateType &&
                value.SnapshotType == snapshotType)
            .OrderByDescending(value => value.StreamVersion)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? null
            : new SnapshotRecord(
                snapshot.TenantId,
                snapshot.StreamId,
                snapshot.AggregateType,
                snapshot.StreamVersion,
                snapshot.SnapshotType,
                snapshot.SchemaVersion,
                snapshot.Payload,
                snapshot.CreatedAt);
    }

    public async Task InvalidateAsync(SnapshotRecord snapshot, CancellationToken cancellationToken = default) =>
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
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}
