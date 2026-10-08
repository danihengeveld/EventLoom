using EventLoom.Storage;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

internal sealed class MongoProjectionStorage(
    MongoStorageCatalog catalog,
    MongoStorageInitializer initializer,
    TimeProvider timeProvider) : IProjectionStorage
{
    private readonly MongoStorageCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    private readonly MongoStorageInitializer initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ProjectionCheckpoint?> GetCheckpointAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = await catalog.ProjectionCheckpoints.Find(FilterCheckpoint(tenantId, key))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return checkpoint is null ? null : ToCheckpoint(checkpoint);
    }

    public async Task<IReadOnlyList<ProjectionCheckpoint>> ReadCheckpointsAsync(
        IReadOnlyCollection<ProjectionKey> keys,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return [];
        }

        var keySet = keys.ToHashSet();
        var names = keySet.Select(value => value.Name).Distinct(StringComparer.Ordinal).ToArray();
        var versions = keySet.Select(value => value.Version).Distinct().ToArray();
        var checkpoints = await catalog.ProjectionCheckpoints.Find(
                Builders<ProjectionCheckpointDocument>.Filter.In(value => value.ProjectionName, names) &
                Builders<ProjectionCheckpointDocument>.Filter.In(value => value.ProjectionVersion, versions))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return checkpoints
            .Where(value => keySet.Contains(new ProjectionKey(value.ProjectionName, value.ProjectionVersion)))
            .Select(ToCheckpoint)
            .ToArray();
    }

    public async Task<IReadOnlyList<ProjectionFailure>> ReadFailuresAsync(
        string tenantId,
        ProjectionKey key,
        bool includeResolved,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var filter = FilterFailure(tenantId, key);
        if (!includeResolved)
        {
            filter &= Builders<ProjectionFailureDocument>.Filter.Eq(value => value.ResolvedAtTicks, (long?)null);
        }

        var failures = await catalog.ProjectionFailures.Find(filter)
            .SortBy(value => value.TenantOffset)
            .ThenBy(value => value.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return failures.Select(ToFailure).ToArray();
    }

    public async Task<int> CountUnresolvedFailuresAsync(
        IReadOnlyCollection<ProjectionKey> keys,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return 0;
        }

        var keySet = keys.ToHashSet();
        var names = keySet.Select(value => value.Name).Distinct(StringComparer.Ordinal).ToArray();
        var versions = keySet.Select(value => value.Version).Distinct().ToArray();
        var failures = await catalog.ProjectionFailures.Find(
                Builders<ProjectionFailureDocument>.Filter.Eq(value => value.ResolvedAtTicks, (long?)null) &
                Builders<ProjectionFailureDocument>.Filter.In(value => value.ProjectionName, names) &
                Builders<ProjectionFailureDocument>.Filter.In(value => value.ProjectionVersion, versions))
            .Project(value => new ProjectionKey(value.ProjectionName, value.ProjectionVersion))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return failures.Count(keySet.Contains);
    }

    public async Task<ProjectionDeliveryResult> ProcessAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        Func<IProjectionTransactionContext, CancellationToken, Task> apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(apply);

        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var session = await catalog.Client.StartSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        session.StartTransaction(catalog.TransactionOptions);
        try
        {
            await VerifyLeaseAsync(session, tenantId, key, lease, cancellationToken).ConfigureAwait(false);
            var checkpoint = await catalog.ProjectionCheckpoints.Find(session, FilterCheckpoint(tenantId, key))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (checkpoint?.Status == ProjectionStatus.Paused)
            {
                await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
                return ProjectionDeliveryResult.Paused;
            }

            if (checkpoint is not null && envelope.TenantOffset <= checkpoint.TenantOffset)
            {
                await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
                return ProjectionDeliveryResult.AlreadyProcessed;
            }

            await apply(new MongoProjectionTransaction(session, catalog.Database), cancellationToken)
                .ConfigureAwait(false);
            await VerifyLeaseAsync(session, tenantId, key, lease, cancellationToken).ConfigureAwait(false);
            var nowTicks = MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow());
            await catalog.ProjectionCheckpoints.UpdateOneAsync(
                session,
                FilterCheckpoint(tenantId, key),
                Builders<ProjectionCheckpointDocument>.Update
                    .SetOnInsert(value => value.TenantId, tenantId)
                    .SetOnInsert(value => value.ProjectionName, key.Name)
                    .SetOnInsert(value => value.ProjectionVersion, key.Version)
                    .Set(value => value.TenantOffset, envelope.TenantOffset)
                    .Set(value => value.Status, ProjectionStatus.Running)
                    .Set(value => value.UpdatedAtTicks, nowTicks),
                new UpdateOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
            await catalog.ProjectionFailures.UpdateManyAsync(
                session,
                FilterFailure(tenantId, key) &
                Builders<ProjectionFailureDocument>.Filter.Eq(value => value.EventId, envelope.EventId) &
                Builders<ProjectionFailureDocument>.Filter.Eq(value => value.ResolvedAtTicks, (long?)null),
                Builders<ProjectionFailureDocument>.Update
                    .Set(value => value.ResolvedAtTicks, nowTicks)
                    .Set(value => value.WasSkipped, false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
            return ProjectionDeliveryResult.Processed;
        }
        catch
        {
            await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RecordFailureAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        int attemptCount,
        string exceptionType,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var session = await catalog.Client.StartSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        session.StartTransaction(catalog.TransactionOptions);
        try
        {
            await VerifyLeaseAsync(session, tenantId, key, lease, cancellationToken).ConfigureAwait(false);
            var nowTicks = MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow());
            await catalog.ProjectionCheckpoints.UpdateOneAsync(
                session,
                FilterCheckpoint(tenantId, key),
                Builders<ProjectionCheckpointDocument>.Update
                    .SetOnInsert(value => value.TenantId, tenantId)
                    .SetOnInsert(value => value.ProjectionName, key.Name)
                    .SetOnInsert(value => value.ProjectionVersion, key.Version)
                    .SetOnInsert(value => value.TenantOffset, 0L)
                    .Set(value => value.Status, ProjectionStatus.Paused)
                    .Set(value => value.UpdatedAtTicks, nowTicks),
                new UpdateOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
            var existing = await catalog.ProjectionFailures.Find(session,
                    FilterFailure(tenantId, key) &
                    Builders<ProjectionFailureDocument>.Filter.Eq(value => value.EventId, envelope.EventId) &
                    Builders<ProjectionFailureDocument>.Filter.Eq(value => value.ResolvedAtTicks, (long?)null))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await catalog.ProjectionFailures.InsertOneAsync(
                    session,
                    new ProjectionFailureDocument
                    {
                        TenantId = tenantId,
                        ProjectionName = key.Name,
                        ProjectionVersion = key.Version,
                        EventId = envelope.EventId,
                        EventType = envelope.EventType,
                        TenantOffset = envelope.TenantOffset,
                        AttemptCount = attemptCount,
                        ExceptionType = exceptionType,
                        FailedAtTicks = nowTicks,
                        WasSkipped = false
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await catalog.ProjectionFailures.UpdateOneAsync(
                    session,
                    Builders<ProjectionFailureDocument>.Filter.Eq(value => value.Id, existing.Id),
                    Builders<ProjectionFailureDocument>.Update
                        .Inc(value => value.AttemptCount, attemptCount)
                        .Set(value => value.ExceptionType, exceptionType)
                        .Set(value => value.FailedAtTicks, nowTicks),
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

    public async Task<bool> ResumeAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var updated = await catalog.ProjectionCheckpoints.UpdateOneAsync(
            FilterCheckpoint(tenantId, key) &
            Builders<ProjectionCheckpointDocument>.Filter.Eq(value => value.Status, ProjectionStatus.Paused),
            Builders<ProjectionCheckpointDocument>.Update
                .Set(value => value.Status, ProjectionStatus.Running)
                .Set(value => value.UpdatedAtTicks, MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow())),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return updated.ModifiedCount == 1;
    }

    public async Task<bool> SkipAsync(
        string tenantId,
        ProjectionKey key,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var session = await catalog.Client.StartSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        session.StartTransaction(catalog.TransactionOptions);
        try
        {
            var checkpoint = await catalog.ProjectionCheckpoints.Find(session, FilterCheckpoint(tenantId, key))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var failure = await catalog.ProjectionFailures.Find(
                    session,
                    FilterFailure(tenantId, key) &
                    Builders<ProjectionFailureDocument>.Filter.Eq(value => value.EventId, eventId) &
                    Builders<ProjectionFailureDocument>.Filter.Eq(value => value.ResolvedAtTicks, (long?)null))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (checkpoint?.Status != ProjectionStatus.Paused || failure is null)
            {
                await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var nowTicks = MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow());
            await catalog.ProjectionCheckpoints.UpdateOneAsync(
                session,
                Builders<ProjectionCheckpointDocument>.Filter.Eq(value => value.Id, checkpoint.Id),
                Builders<ProjectionCheckpointDocument>.Update
                    .Set(value => value.TenantOffset, failure.TenantOffset)
                    .Set(value => value.Status, ProjectionStatus.Running)
                    .Set(value => value.UpdatedAtTicks, nowTicks),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await catalog.ProjectionFailures.UpdateOneAsync(
                session,
                Builders<ProjectionFailureDocument>.Filter.Eq(value => value.Id, failure.Id),
                Builders<ProjectionFailureDocument>.Update
                    .Set(value => value.ResolvedAtTicks, nowTicks)
                    .Set(value => value.WasSkipped, true),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ReplayAsync(string tenantId, ProjectionKey key, CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await catalog.ProjectionCheckpoints.UpdateOneAsync(
            FilterCheckpoint(tenantId, key),
            Builders<ProjectionCheckpointDocument>.Update
                .SetOnInsert(value => value.TenantId, tenantId)
                .SetOnInsert(value => value.ProjectionName, key.Name)
                .SetOnInsert(value => value.ProjectionVersion, key.Version)
                .Set(value => value.TenantOffset, 0L)
                .Set(value => value.Status, ProjectionStatus.Running)
                .Set(value => value.UpdatedAtTicks, MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow())),
            new UpdateOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyLeaseAsync(
        IClientSessionHandle session,
        string tenantId,
        ProjectionKey key,
        WorkerLease lease,
        CancellationToken cancellationToken)
    {
        var nowTicks = MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow());
        var active = await catalog.ProjectionLeases.Find(
                session,
                Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.LeaseName, lease.LeaseName) &
                Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.OwnerId, lease.OwnerId) &
                Builders<ProjectionLeaseDocument>.Filter.Eq(value => value.FencingToken, lease.FencingToken) &
                Builders<ProjectionLeaseDocument>.Filter.Gt(value => value.LeaseUntilTicks, nowTicks))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            throw new ProjectionLeaseLostException(tenantId, key);
        }
    }

    private static FilterDefinition<ProjectionCheckpointDocument>
        FilterCheckpoint(string tenantId, ProjectionKey key) =>
        Builders<ProjectionCheckpointDocument>.Filter.Eq(value => value.TenantId, tenantId) &
        Builders<ProjectionCheckpointDocument>.Filter.Eq(value => value.ProjectionName, key.Name) &
        Builders<ProjectionCheckpointDocument>.Filter.Eq(value => value.ProjectionVersion, key.Version);

    private static FilterDefinition<ProjectionFailureDocument> FilterFailure(string tenantId, ProjectionKey key) =>
        Builders<ProjectionFailureDocument>.Filter.Eq(value => value.TenantId, tenantId) &
        Builders<ProjectionFailureDocument>.Filter.Eq(value => value.ProjectionName, key.Name) &
        Builders<ProjectionFailureDocument>.Filter.Eq(value => value.ProjectionVersion, key.Version);

    private static ProjectionCheckpoint ToCheckpoint(ProjectionCheckpointDocument document) =>
        new(
            document.TenantId,
            new ProjectionKey(document.ProjectionName, document.ProjectionVersion),
            document.TenantOffset,
            document.Status,
            MongoStorageTime.FromUtcTicks(document.UpdatedAtTicks));

    private static ProjectionFailure ToFailure(ProjectionFailureDocument document) =>
        new(
            document.TenantId,
            new ProjectionKey(document.ProjectionName, document.ProjectionVersion),
            document.EventId,
            document.EventType,
            document.TenantOffset,
            document.AttemptCount,
            document.ExceptionType,
            MongoStorageTime.FromUtcTicks(document.FailedAtTicks),
            MongoStorageTime.FromUtcTicks(document.ResolvedAtTicks),
            document.WasSkipped);
}
