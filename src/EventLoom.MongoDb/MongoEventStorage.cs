using EventLoom.Storage;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

internal sealed class MongoEventStorage(
    MongoStorageCatalog catalog,
    MongoStorageInitializer initializer,
    MongoSessionAccessor sessionAccessor) : IEventStorage
{
    private readonly MongoStorageCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly MongoStorageInitializer initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
    private readonly MongoSessionAccessor sessionAccessor = sessionAccessor ?? throw new ArgumentNullException(nameof(sessionAccessor));

    public async Task<IStorageTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var session = await catalog.Client.StartSessionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        session.StartTransaction(catalog.TransactionOptions);
        return new MongoStorageTransaction(catalog, session);
    }

    public async Task<StorageAppendResult> AppendAsync(
        StorageAppendRequest request,
        IStorageTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            if (transaction is not MongoStorageTransaction mongoTransaction || !ReferenceEquals(mongoTransaction.Catalog, catalog))
            {
                throw new InvalidOperationException(
                    "The supplied transaction was not created by this EventLoom MongoDB storage scope.");
            }

            try
            {
                return await AppendCoreAsync(request, mongoTransaction.Session, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldWrapConcurrency(exception))
            {
                throw new EventStoreConcurrencyException(request.TenantId, request.StreamId, exception);
            }
        }

        using var session = await catalog.Client.StartSessionAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        session.StartTransaction(catalog.TransactionOptions);
        try
        {
            var result = await AppendCoreAsync(request, session, cancellationToken).ConfigureAwait(false);
            await MongoTransactionUtilities.CommitWithRetryAsync(session, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (WrongExpectedVersionException)
        {
            await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await MongoTransactionUtilities.AbortIfNeededAsync(session, cancellationToken).ConfigureAwait(false);
            if (request.AppendId is not null)
            {
                var replay = await TryReadIdempotentReplayAsync(request.TenantId, request.AppendId, cancellationToken)
                    .ConfigureAwait(false);
                if (replay is not null)
                {
                    return replay;
                }
            }

            if (ShouldWrapConcurrency(exception))
            {
                throw new EventStoreConcurrencyException(request.TenantId, request.StreamId, exception);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(
        string tenantId,
        string streamId,
        long? fromVersion,
        long? toVersion,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var builder = Builders<EventDocument>.Filter;
        var filter = builder.Eq(value => value.TenantId, tenantId) &
                     builder.Eq(value => value.StreamId, streamId);
        if (fromVersion.HasValue)
        {
            filter &= builder.Gte(value => value.StreamVersion, fromVersion.Value);
        }

        if (toVersion.HasValue)
        {
            filter &= builder.Lte(value => value.StreamVersion, toVersion.Value);
        }

        var documents = await catalog.Events.Find(filter)
            .SortBy(value => value.StreamVersion)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(ToStored).ToArray();
    }

    public async Task<IReadOnlyList<StoredEvent>> ReadTenantAsync(
        string tenantId,
        long afterOffset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var documents = await catalog.Events.Find(
                Builders<EventDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<EventDocument>.Filter.Gt(value => value.TenantOffset, afterOffset))
            .SortBy(value => value.TenantOffset)
            .Limit(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(ToStored).ToArray();
    }

    public async Task<IReadOnlyList<TenantHead>> ReadTenantHeadsAsync(CancellationToken cancellationToken = default)
    {
        await initializer.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        var heads = await catalog.Offsets.Find(Builders<OffsetDocument>.Filter.Gt(value => value.NextOffset, 0L))
            .SortBy(value => value.TenantId)
            .Project(value => new TenantHead(value.TenantId, value.NextOffset))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return heads;
    }

    private async Task<StorageAppendResult> AppendCoreAsync(
        StorageAppendRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (request.AppendId is not null)
        {
            var replay = await TryReadIdempotentReplayAsync(session, request.TenantId, request.AppendId, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                return replay;
            }
        }

        var streamFilter = Builders<StreamDocument>.Filter.Eq(value => value.TenantId, request.TenantId) &
                           Builders<StreamDocument>.Filter.Eq(value => value.StreamId, request.StreamId);
        var stream = await catalog.Streams.Find(session, streamFilter)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var currentVersion = stream?.Version;
        if (!request.ExpectedVersion.IsMatch(currentVersion))
        {
            throw new WrongExpectedVersionException(request.ExpectedVersion, currentVersion);
        }

        if (stream is null)
        {
            try
            {
                await catalog.Streams.InsertOneAsync(
                    session,
                    new StreamDocument
                    {
                        TenantId = request.TenantId,
                        StreamId = request.StreamId,
                        AggregateType = request.AggregateType,
                        Version = request.Events.Count
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (MongoDbExceptionClassifier.Classify(exception) == MongoDbExceptionClassification.DuplicateKey)
            {
                throw new EventStoreConcurrencyException(request.TenantId, request.StreamId, exception);
            }
        }
        else
        {
            var advanced = await catalog.Streams.UpdateOneAsync(
                session,
                streamFilter & Builders<StreamDocument>.Filter.Eq(value => value.Version, currentVersion!.Value),
                Builders<StreamDocument>.Update.Set(value => value.Version, currentVersion.Value + request.Events.Count),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (advanced.MatchedCount != 1)
            {
                throw new EventStoreConcurrencyException(
                    request.TenantId,
                    request.StreamId,
                    new InvalidOperationException("The stream head changed concurrently."));
            }
        }

        var offset = await catalog.Offsets.FindOneAndUpdateAsync(
            session,
            Builders<OffsetDocument>.Filter.Eq(value => value.Id, request.TenantId),
            Builders<OffsetDocument>.Update
                .SetOnInsert(value => value.Id, request.TenantId)
                .SetOnInsert(value => value.TenantId, request.TenantId)
                .Inc(value => value.NextOffset, request.Events.Count),
            new FindOneAndUpdateOptions<OffsetDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException(
            "The tenant offset transaction did not return an offset document.");

        var firstOffset = offset.NextOffset - request.Events.Count + 1;
        var headers = MongoMetadata.ToDocument(request.Metadata.Headers);
        var documents = new List<EventDocument>(request.Events.Count);
        var stored = new List<StoredEvent>(request.Events.Count);
        var version = currentVersion ?? 0;
        for (var index = 0; index < request.Events.Count; index++)
        {
            var @event = request.Events[index];
            var streamVersion = ++version;
            var tenantOffset = firstOffset + index;
            var document = new EventDocument
            {
                EventId = @event.EventId,
                TenantId = request.TenantId,
                StreamId = request.StreamId,
                AggregateType = request.AggregateType,
                StreamVersion = streamVersion,
                TenantOffset = tenantOffset,
                EventType = @event.EventType,
                EventTypeVersion = @event.EventTypeVersion,
                Payload = @event.Payload,
                OccurredAtTicks = MongoStorageTime.ToUtcTicks(@event.OccurredAt),
                CorrelationId = request.Metadata.CorrelationId,
                CausationId = request.Metadata.CausationId,
                Actor = request.Metadata.Actor,
                Headers = headers,
                AppendId = request.AppendId
            };
            documents.Add(document);
            stored.Add(ToStored(document));
        }

        await catalog.Events.InsertManyAsync(session, documents, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (request.WriteOutbox)
        {
            await catalog.Outbox.InsertManyAsync(
                    session,
                    documents.Select(ToOutboxDocument),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if (request.BeforeCommit is not null)
        {
            using var scope = sessionAccessor.Enter(session);
            await request.BeforeCommit(stored, cancellationToken).ConfigureAwait(false);
        }

        return new StorageAppendResult(stored, false);
    }

    private async Task<StorageAppendResult?> TryReadIdempotentReplayAsync(
        string tenantId,
        string appendId,
        CancellationToken cancellationToken)
    {
        var documents = await catalog.Events.Find(
                Builders<EventDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<EventDocument>.Filter.Eq(value => value.AppendId, appendId))
            .SortBy(value => value.StreamVersion)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Count == 0 ? null : new StorageAppendResult(documents.Select(ToStored).ToArray(), true);
    }

    private async Task<StorageAppendResult?> TryReadIdempotentReplayAsync(
        IClientSessionHandle session,
        string tenantId,
        string appendId,
        CancellationToken cancellationToken)
    {
        var documents = await catalog.Events.Find(
                session,
                Builders<EventDocument>.Filter.Eq(value => value.TenantId, tenantId) &
                Builders<EventDocument>.Filter.Eq(value => value.AppendId, appendId))
            .SortBy(value => value.StreamVersion)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Count == 0 ? null : new StorageAppendResult(documents.Select(ToStored).ToArray(), true);
    }

    private static bool ShouldWrapConcurrency(Exception exception) =>
        MongoDbExceptionClassifier.IsConcurrency(MongoDbExceptionClassifier.Classify(exception));

    internal static StoredEvent ToStored(EventDocument document) =>
        new(
            document.EventId,
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
                document.Headers));

    private static OutboxDocument ToOutboxDocument(EventDocument document) =>
        new()
        {
            MessageId = document.EventId,
            TenantId = document.TenantId,
            StreamId = document.StreamId,
            AggregateType = document.AggregateType,
            StreamVersion = document.StreamVersion,
            TenantOffset = document.TenantOffset,
            EventType = document.EventType,
            EventTypeVersion = document.EventTypeVersion,
            Payload = document.Payload,
            OccurredAtTicks = document.OccurredAtTicks,
            CorrelationId = document.CorrelationId,
            CausationId = document.CausationId,
            Actor = document.Actor,
            Headers = document.Headers,
            AttemptCount = 0
        };
}
