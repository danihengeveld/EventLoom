using EventLoom.Storage;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

internal sealed class MongoSnapshotStorage(
    MongoStorageCatalog catalog,
    MongoStorageInitializer initializer) : ISnapshotStorage
{
    private readonly MongoStorageCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly MongoStorageInitializer initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

    public async Task WriteAsync(
        SnapshotRecord snapshot,
        int snapshotsToRetain,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var session = await catalog.Client.StartSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        session.StartTransaction(catalog.TransactionOptions);
        try
        {
            await catalog.Snapshots.InsertOneAsync(
                session,
                new SnapshotDocument
                {
                    TenantId = snapshot.TenantId,
                    StreamId = snapshot.StreamId,
                    AggregateType = snapshot.AggregateType,
                    StreamVersion = snapshot.StreamVersion,
                    SnapshotType = snapshot.SnapshotType,
                    SchemaVersion = snapshot.SchemaVersion,
                    Payload = snapshot.Payload,
                    CreatedAtTicks = MongoStorageTime.ToUtcTicks(snapshot.CreatedAt)
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var expiredIds = await catalog.Snapshots.Find(
                    session,
                    Builders<SnapshotDocument>.Filter.Eq(value => value.TenantId, snapshot.TenantId) &
                    Builders<SnapshotDocument>.Filter.Eq(value => value.StreamId, snapshot.StreamId) &
                    Builders<SnapshotDocument>.Filter.Eq(value => value.AggregateType, snapshot.AggregateType))
                .SortByDescending(value => value.StreamVersion)
                .ThenByDescending(value => value.Id)
                .Project(value => value.Id)
                .Skip(snapshotsToRetain)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (expiredIds.Count > 0)
            {
                await catalog.Snapshots.DeleteManyAsync(
                    session,
                    Builders<SnapshotDocument>.Filter.In(value => value.Id, expiredIds),
                    options: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SnapshotRecord?> ReadLatestAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        string snapshotType,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var document = await catalog.Snapshots.Find(
                Builders<SnapshotDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.StreamId, streamId) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.AggregateType, aggregateType) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.SnapshotType, snapshotType))
            .SortByDescending(value => value.StreamVersion)
            .ThenByDescending(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToRecord(document);
    }

    public async Task InvalidateAsync(SnapshotRecord snapshot, CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await catalog.Snapshots.DeleteManyAsync(
                Builders<SnapshotDocument>.Filter.Eq(value => value.TenantId, snapshot.TenantId) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.StreamId, snapshot.StreamId) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.AggregateType, snapshot.AggregateType) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.StreamVersion, snapshot.StreamVersion) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.SnapshotType, snapshot.SnapshotType) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.SchemaVersion, snapshot.SchemaVersion) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.Payload, snapshot.Payload) &
                Builders<SnapshotDocument>.Filter.Eq(value => value.CreatedAtTicks, MongoStorageTime.ToUtcTicks(snapshot.CreatedAt)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static SnapshotRecord ToRecord(SnapshotDocument document) =>
        new(
            document.TenantId,
            document.StreamId,
            document.AggregateType,
            document.StreamVersion,
            document.SnapshotType,
            document.SchemaVersion,
            document.Payload,
            MongoStorageTime.FromUtcTicks(document.CreatedAtTicks));
}
