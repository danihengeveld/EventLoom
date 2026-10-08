using System.Data;
using System.Text.Json;
using EventLoom.Storage;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Persists events, streams, tenant offsets, and outbox rows through an EF Core context.</summary>
internal sealed class EfEventStorage(EventStoreDbContext context, EfStorageDialect dialect) : IEventStorage
{
    public async Task<IStorageTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        return new EfStorageTransaction(context, transaction);
    }

    public async Task<StorageAppendResult> AppendAsync(
        StorageAppendRequest request,
        IStorageTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (transaction is not null &&
            !(transaction is EfStorageTransaction efTransaction && ReferenceEquals(efTransaction.Context, context)))
        {
            throw new InvalidOperationException(
                "The supplied transaction was not created by this EventLoom EF Core storage scope.");
        }

        try
        {
            return await AppendCoreAsync(request, transaction is null, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            throw new EventStoreConcurrencyException(request.TenantId, request.StreamId, exception);
        }
    }

    private async Task<StorageAppendResult> AppendCoreAsync(
        StorageAppendRequest request,
        bool ownsTransaction,
        CancellationToken cancellationToken)
    {
        var tenantId = request.TenantId;
        context.ChangeTracker.Clear();
        await using var transaction = ownsTransaction
            ? await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var stream = await context.Streams
            .SingleOrDefaultAsync(
                value => value.TenantId == tenantId && value.StreamId == request.StreamId,
                cancellationToken).ConfigureAwait(false);
        if (request.AppendId is not null)
        {
            var existing = await context.Events
                .Where(value => value.TenantId == tenantId && value.AppendId == request.AppendId)
                .OrderBy(value => value.StreamVersion)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (existing.Count > 0)
            {
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                return new StorageAppendResult(existing.Select(ToStored).ToArray(), true);
            }
        }

        var currentVersion = stream?.Version;
        if (!request.ExpectedVersion.IsMatch(currentVersion))
        {
            throw new WrongExpectedVersionException(request.ExpectedVersion, currentVersion);
        }

        stream ??= new StreamEntity
        {
            TenantId = tenantId,
            StreamId = request.StreamId,
            AggregateType = request.AggregateType,
            Version = 0
        };
        if (context.Entry(stream).State == EntityState.Detached)
        {
            context.Streams.Add(stream);
        }

        var offset = await dialect.LockTenantOffsetAsync(context, tenantId, cancellationToken).ConfigureAwait(false)
                     ?? new TenantOffsetEntity { TenantId = tenantId, NextOffset = 0 };
        if (context.Entry(offset).State == EntityState.Detached)
        {
            context.TenantOffsets.Add(offset);
        }

        var headers = request.Metadata.Headers.Count == 0
            ? null
            : JsonSerializer.Serialize(request.Metadata.Headers);
        var entities = new List<EventEntity>(request.Events.Count);
        foreach (var @event in request.Events)
        {
            var entity = new EventEntity
            {
                EventId = @event.EventId,
                TenantId = tenantId,
                StreamId = request.StreamId,
                AggregateType = request.AggregateType,
                StreamVersion = ++stream.Version,
                TenantOffset = ++offset.NextOffset,
                EventType = @event.EventType,
                EventTypeVersion = @event.EventTypeVersion,
                Payload = @event.Payload,
                OccurredAt = @event.OccurredAt,
                CorrelationId = request.Metadata.CorrelationId,
                CausationId = request.Metadata.CausationId,
                Actor = request.Metadata.Actor,
                Headers = headers,
                AppendId = request.AppendId
            };
            context.Events.Add(entity);
            entities.Add(entity);
            if (request.WriteOutbox)
            {
                context.Outbox.Add(new OutboxEntity
                {
                    MessageId = entity.EventId,
                    TenantId = entity.TenantId,
                    StreamId = entity.StreamId,
                    AggregateType = entity.AggregateType,
                    StreamVersion = entity.StreamVersion,
                    TenantOffset = entity.TenantOffset,
                    EventType = entity.EventType,
                    EventTypeVersion = entity.EventTypeVersion,
                    Payload = entity.Payload,
                    OccurredAt = entity.OccurredAt,
                    CorrelationId = entity.CorrelationId,
                    CausationId = entity.CausationId,
                    Actor = entity.Actor,
                    Headers = entity.Headers
                });
            }
        }

        var stored = entities.Select(ToStored).ToArray();
        if (request.BeforeCommit is not null)
        {
            await request.BeforeCommit(stored, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            throw;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new StorageAppendResult(stored, false);
    }

    public async Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(
        string tenantId,
        string streamId,
        long? fromVersion,
        long? toVersion,
        CancellationToken cancellationToken = default)
    {
        var query = context.Events
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.StreamId == streamId);
        if (fromVersion.HasValue)
        {
            query = query.Where(value => value.StreamVersion >= fromVersion.Value);
        }

        if (toVersion.HasValue)
        {
            query = query.Where(value => value.StreamVersion <= toVersion.Value);
        }

        var entities = await query.OrderBy(value => value.StreamVersion)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToStored).ToArray();
    }

    public async Task<IReadOnlyList<StoredEvent>> ReadTenantAsync(
        string tenantId,
        long afterOffset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var entities = await context.Events
            .AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.TenantOffset > afterOffset)
            .OrderBy(value => value.TenantOffset)
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToStored).ToArray();
    }

    public async Task<IReadOnlyList<TenantHead>> ReadTenantHeadsAsync(CancellationToken cancellationToken = default)
    {
        var heads = await context.TenantOffsets.AsNoTracking()
            .Where(value => value.NextOffset > 0)
            .OrderBy(value => value.TenantId)
            .Select(value => new { TenantId = value.TenantId!, Offset = value.NextOffset })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return heads.Select(value => new TenantHead(value.TenantId, value.Offset)).ToArray();
    }

    internal static StoredEvent ToStored(EventEntity entity) =>
        new(
            entity.EventId,
            entity.TenantId,
            entity.StreamId,
            entity.AggregateType,
            entity.StreamVersion,
            entity.TenantOffset,
            entity.EventType,
            entity.EventTypeVersion,
            entity.Payload,
            entity.OccurredAt,
            CreateMetadata(entity.EventId, entity.CorrelationId, entity.CausationId, entity.Actor, entity.Headers));

    internal static EventMetadata CreateMetadata(
        Guid id,
        string? correlationId,
        string? causationId,
        string? actor,
        string? headers) =>
        headers is null
            ? new EventMetadata(correlationId, causationId, actor)
            : new EventMetadata(
                correlationId,
                causationId,
                actor,
                JsonSerializer.Deserialize<Dictionary<string, string>>(headers)
                ?? throw new InvalidOperationException($"Event '{id}' has invalid metadata headers."));
}
