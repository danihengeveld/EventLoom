using EventLoom.Storage;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

internal sealed class MongoWorkerLeaseStorage(
    MongoStorageCatalog catalog,
    MongoStorageInitializer initializer,
    TimeProvider timeProvider) : IWorkerLeaseStorage
{
    private readonly MongoStorageCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly MongoStorageInitializer initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<WorkerLease?> TryAcquireAsync(
        string tenantId,
        string leaseName,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        tenantId = new TenantId(tenantId).Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var nowTicks = MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow());
        var leaseUntilTicks = nowTicks + duration.Ticks;
        try
        {
            var lease = await catalog.ProjectionLeases.FindOneAndUpdateAsync(
                Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.LeaseName, leaseName) &
                Builders<ProjectionLeaseDocument>.Filter.Or(
                    Builders<ProjectionLeaseDocument>.Filter.Lte(value => value.LeaseUntilTicks, nowTicks),
                    Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.OwnerId, ownerId)),
                Builders<ProjectionLeaseDocument>.Update
                    .SetOnInsert(value => value.TenantId, tenantId)
                    .SetOnInsert(value => value.LeaseName, leaseName)
                    .Set(value => value.OwnerId, ownerId)
                    .Set(value => value.LeaseUntilTicks, leaseUntilTicks)
                    .Inc(value => value.FencingToken, 1L),
                new FindOneAndUpdateOptions<ProjectionLeaseDocument>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken).ConfigureAwait(false);
            return lease is null
                ? null
                : new WorkerLease(
                    tenantId,
                    leaseName,
                    lease.OwnerId,
                    lease.FencingToken,
                    MongoStorageTime.FromUtcTicks(lease.LeaseUntilTicks));
        }
        catch (MongoWriteException exception) when (MongoDbExceptionClassifier.Classify(exception) == MongoDbExceptionClassification.DuplicateKey)
        {
            return null;
        }
        catch (MongoCommandException exception) when (MongoDbExceptionClassifier.Classify(exception) == MongoDbExceptionClassification.DuplicateKey)
        {
            return null;
        }
    }

    public async Task<bool> ReleaseAsync(WorkerLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var expired = MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow());
        var released = await catalog.ProjectionLeases.UpdateOneAsync(
            Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.TenantId, lease.TenantId) &
            Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.LeaseName, lease.LeaseName) &
            Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.OwnerId, lease.OwnerId) &
            Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.FencingToken, lease.FencingToken) &
            Builders<ProjectionLeaseDocument>.Filter.Gt(value => value.LeaseUntilTicks, expired),
            Builders<ProjectionLeaseDocument>.Update.Set(value => value.LeaseUntilTicks, expired),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return released.ModifiedCount == 1;
    }
}
