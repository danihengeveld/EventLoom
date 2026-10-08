using EventLoom.Storage;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

internal sealed class MongoOutboxStorage(
    MongoStorageCatalog catalog,
    MongoStorageInitializer initializer,
    TimeProvider timeProvider) : IOutboxStorage
{
    private readonly MongoStorageCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    private readonly MongoStorageInitializer initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IReadOnlyList<string>> ReadPendingTenantIdsAsync(CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var tenants = await catalog.Outbox.DistinctAsync(
                value => value.TenantId,
                Builders<OutboxDocument>.Filter.Eq(value => value.PublishedAtTicks, (long?)null),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (await tenants.ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var count = await catalog.Outbox.CountDocumentsAsync(
            Builders<OutboxDocument>.Filter.Eq(value => value.PublishedAtTicks, (long?)null),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return checked((int)count);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ReadPendingAsync(
        string tenantId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var messages = await catalog.Outbox.Find(
                Builders<OutboxDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<OutboxDocument>.Filter.Eq(value => value.PublishedAtTicks, (long?)null))
            .SortBy(value => value.TenantOffset)
            .Limit(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return messages.Select(ToMessage).ToArray();
    }

    public async Task<OutboxMessage?> GetAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var message = await catalog.Outbox.Find(
                Builders<OutboxDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<OutboxDocument>.Filter.Eq(value => value.MessageId, messageId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return message is null ? null : ToMessage(message);
    }

    public async Task<IReadOnlyList<OutboxAttempt>> ReadAttemptsAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var attempts = await catalog.OutboxAttempts.Find(
                Builders<OutboxAttemptDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<OutboxAttemptDocument>.Filter.Eq(value => value.MessageId, messageId))
            .SortBy(value => value.AttemptNumber)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return attempts.Select(value => new OutboxAttempt(
            value.MessageId,
            value.TenantId,
            value.AttemptNumber,
            MongoStorageTime.FromUtcTicks(value.AttemptedAtTicks),
            value.Succeeded,
            value.ExceptionType)).ToArray();
    }

    public async Task<bool> RecordAttemptAsync(
        OutboxMessage message,
        WorkerLease lease,
        string? exceptionType,
        TimeSpan successfulDeliveryRetention,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        using var session = await catalog.Client.StartSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        session.StartTransaction(catalog.TransactionOptions);
        try
        {
            await VerifyLeaseAsync(session, message.TenantId, lease, cancellationToken).ConfigureAwait(false);
            var document = await catalog.Outbox.Find(
                                   session,
                                   Builders<OutboxDocument>.Filter.Eq(value => value.TenantId, message.TenantId) &
                                   Builders<OutboxDocument>.Filter.Eq(value => value.MessageId, message.MessageId))
                               .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                           ?? throw new InvalidOperationException(
                               $"Outbox message '{message.MessageId}' does not exist.");
            if (document.PublishedAtTicks is not null)
            {
                await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
                return false;
            }

            var attemptedAtTicks = MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow());
            var attemptNumber = document.AttemptCount + 1;
            if (exceptionType is null && successfulDeliveryRetention == TimeSpan.Zero)
            {
                await catalog.OutboxAttempts.DeleteManyAsync(
                    session,
                    Builders<OutboxAttemptDocument>.Filter.Eq(value => value.MessageId, document.MessageId),
                    options: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await catalog.Outbox.DeleteOneAsync(
                    session,
                    Builders<OutboxDocument>.Filter.Eq(value => value.Id, document.Id),
                    options: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            await catalog.OutboxAttempts.InsertOneAsync(
                session,
                new OutboxAttemptDocument
                {
                    MessageId = document.MessageId,
                    TenantId = document.TenantId,
                    AttemptNumber = attemptNumber,
                    AttemptedAtTicks = attemptedAtTicks,
                    Succeeded = exceptionType is null,
                    ExceptionType = exceptionType
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await catalog.Outbox.UpdateOneAsync(
                session,
                Builders<OutboxDocument>.Filter.Eq(value => value.Id, document.Id),
                Builders<OutboxDocument>.Update
                    .Set(value => value.AttemptCount, attemptNumber)
                    .Set(value => value.PublishedAtTicks,
                        exceptionType is null ? attemptedAtTicks : document.PublishedAtTicks),
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

    public async Task<int> PurgePublishedAsync(
        TimeSpan successfulDeliveryRetention,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var nowTicks = MongoStorageTime.ToUtcTicks(timeProvider.GetUtcNow());
        var cutoffTicks = successfulDeliveryRetention >= TimeSpan.FromTicks(nowTicks)
            ? 0L
            : nowTicks - successfulDeliveryRetention.Ticks;
        var messageIds = await catalog.Outbox.Find(
                Builders<OutboxDocument>.Filter.Ne(value => value.PublishedAtTicks, (long?)null) &
                Builders<OutboxDocument>.Filter.Lte(value => value.PublishedAtTicks, cutoffTicks))
            .SortBy(value => value.PublishedAtTicks)
            .ThenBy(value => value.Id)
            .Limit(limit)
            .Project(value => value.MessageId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (messageIds.Count == 0)
        {
            return 0;
        }

        using var session = await catalog.Client.StartSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        session.StartTransaction(catalog.TransactionOptions);
        try
        {
            await catalog.OutboxAttempts.DeleteManyAsync(
                session,
                Builders<OutboxAttemptDocument>.Filter.In(value => value.MessageId, messageIds),
                options: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var deleted = await catalog.Outbox.DeleteManyAsync(
                session,
                Builders<OutboxDocument>.Filter.In(value => value.MessageId, messageIds) &
                Builders<OutboxDocument>.Filter.Ne(value => value.PublishedAtTicks, (long?)null),
                options: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
            return checked((int)deleted.DeletedCount);
        }
        catch
        {
            await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task VerifyLeaseAsync(
        IClientSessionHandle session,
        string tenantId,
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
            throw new OutboxLeaseLostException(tenantId);
        }
    }

    private static OutboxMessage ToMessage(OutboxDocument document) =>
        new(
            document.MessageId,
            document.TenantId,
            document.StreamId,
            document.AggregateType,
            document.StreamVersion,
            document.TenantOffset,
            document.EventType,
            document.EventTypeVersion,
            document.Payload,
            MongoStorageTime.FromUtcTicks(document.OccurredAtTicks),
            new EventMetadata(
                document.CorrelationId,
                document.CausationId,
                document.Actor,
                document.Headers),
            document.AttemptCount,
            MongoStorageTime.FromUtcTicks(document.PublishedAtTicks));
}
