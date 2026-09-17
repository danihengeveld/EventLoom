using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EventLoom;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Provides transactional append and bounded read operations for an EventLoom event store.
/// </summary>
public sealed class EventStore(
    EventStoreDbContext context,
    EventSerializer serializer,
    IEventIdGenerator eventIdGenerator,
    TimeProvider timeProvider,
    EventStoreOptions? eventStoreOptions = null,
    ITenantAccessor? tenantAccessor = null,
    IEventStoreRetryPolicy? retryPolicy = null,
    IInlineProjectionDispatcher? inlineProjectionDispatcher = null)
{
    private readonly EventStoreDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly EventSerializer serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IEventIdGenerator eventIdGenerator =
        eventIdGenerator ?? throw new ArgumentNullException(nameof(eventIdGenerator));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly EventStoreOptions eventStoreOptions = eventStoreOptions ?? new();
    private readonly ITenantAccessor? tenantAccessor = tenantAccessor;
    private readonly IEventStoreRetryPolicy retryPolicy = retryPolicy ?? new NoopEventStoreRetryPolicy();
    private readonly IInlineProjectionDispatcher? inlineProjectionDispatcher = inlineProjectionDispatcher;

    /// <summary>
    /// Appends a batch atomically and returns the persisted envelopes.
    /// </summary>
    public async Task<AppendResult> AppendAsync(
        AppendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Events.Count == 0)
        {
            throw new ArgumentException("At least one event is required.", nameof(request));
        }

        var tenantId = ResolveTenant(request.TenantId);
        try
        {
            return await retryPolicy.ExecuteAsync(
                cancellationToken => AppendCoreAsync(request, tenantId, ownsTransaction: true, cancellationToken),
                cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new EventStoreConcurrencyException(tenantId, request.StreamId, exception);
        }
    }

    /// <summary>
    /// Appends a batch into a transaction that the caller owns.
    /// </summary>
    /// <remarks>
    /// The supplied transaction must belong to the exact database connection used by this
    /// <see cref="EventStoreDbContext"/>. EventLoom does not commit or roll back it.
    /// Configure application and EventLoom contexts with the same scoped connection before
    /// using this advanced API.
    /// </remarks>
    /// <param name="request">The atomic append to persist.</param>
    /// <param name="transaction">The caller-owned relational database transaction.</param>
    /// <param name="cancellationToken">Cancels the append operation.</param>
    /// <returns>The persisted event envelopes.</returns>
    public async Task<AppendResult> AppendInTransactionAsync(
        AppendRequest request,
        DbTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(transaction);
        if (request.Events.Count == 0)
        {
            throw new ArgumentException("At least one event is required.", nameof(request));
        }

        if (!ReferenceEquals(context.Database.GetDbConnection(), transaction.Connection))
        {
            throw new InvalidOperationException(
                "The supplied transaction must belong to the exact connection used by EventLoom.");
        }

        var tenantId = ResolveTenant(request.TenantId);
        await using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken);
        try
        {
            return await AppendCoreAsync(request, tenantId, ownsTransaction: false, cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new EventStoreConcurrencyException(tenantId, request.StreamId, exception);
        }
    }

    private async Task<AppendResult> AppendCoreAsync(
        AppendRequest request,
        string tenantId,
        bool ownsTransaction,
        CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        await using var transaction = ownsTransaction
            ? await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            : null;
        var stream = await context.Streams
            .SingleOrDefaultAsync(
                value => value.TenantId == tenantId && value.StreamId == request.StreamId,
                cancellationToken);
        if (request.AppendId is not null)
        {
            var existing = await context.Events
                .Where(value => value.TenantId == tenantId && value.AppendId == request.AppendId)
                .OrderBy(value => value.StreamVersion)
                .ToListAsync(cancellationToken);
            if (existing.Count > 0)
            {
                    if (transaction is not null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                    }

                    return new AppendResult(existing.Select(ToEnvelope).ToArray(), true);
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

        var offset = context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true
            ? await EnsurePostgreSqlOffsetAsync(tenantId, cancellationToken)
            : await context.TenantOffsets
                .SingleOrDefaultAsync(value => value.TenantId == tenantId, cancellationToken);
        offset ??= new TenantOffsetEntity { TenantId = tenantId, NextOffset = 0 };
        if (context.Entry(offset).State == EntityState.Detached)
        {
            context.TenantOffsets.Add(offset);
        }

        var envelopes = new List<EventEnvelope>(request.Events.Count);
        foreach (var @event in request.Events)
        {
            var payload = serializer.SerializePayload(@event);
            var eventEntity = new EventEntity
            {
                EventId = eventIdGenerator.Create(),
                TenantId = tenantId,
                StreamId = request.StreamId,
                AggregateType = request.AggregateType,
                StreamVersion = ++stream.Version,
                TenantOffset = ++offset.NextOffset,
                EventType = payload.EventName,
                EventTypeVersion = payload.Version,
                Payload = payload.Payload,
                OccurredAt = timeProvider.GetUtcNow(),
                CorrelationId = request.Metadata.CorrelationId,
                CausationId = request.Metadata.CausationId,
                Actor = request.Metadata.Actor,
                Headers = request.Metadata.Headers.Count == 0
                    ? null
                    : JsonSerializer.Serialize(request.Metadata.Headers),
                AppendId = request.AppendId
            };
            context.Events.Add(eventEntity);
            context.Outbox.Add(new OutboxEntity
            {
                MessageId = eventEntity.EventId,
                TenantId = eventEntity.TenantId,
                StreamId = eventEntity.StreamId,
                AggregateType = eventEntity.AggregateType,
                StreamVersion = eventEntity.StreamVersion,
                TenantOffset = eventEntity.TenantOffset,
                EventType = eventEntity.EventType,
                EventTypeVersion = eventEntity.EventTypeVersion,
                Payload = eventEntity.Payload,
                OccurredAt = eventEntity.OccurredAt,
                CorrelationId = eventEntity.CorrelationId,
                CausationId = eventEntity.CausationId,
                Actor = eventEntity.Actor,
                Headers = eventEntity.Headers
            });
            var envelope = ToEnvelope(eventEntity, @event, request.Metadata);
            envelopes.Add(envelope);
            if (inlineProjectionDispatcher is not null)
            {
                await inlineProjectionDispatcher.DispatchAsync(envelope, cancellationToken);
            }
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            throw;
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new AppendResult(envelopes, false);
    }

    [SuppressMessage(
        "Usage",
        "EF1003:Interpolated SQL queries should use the interpolated form",
        Justification = "The table identifier comes from EF's mapped model and is quoted; tenant values remain parameters.")]
    private async Task<TenantOffsetEntity?> EnsurePostgreSqlOffsetAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        var entityType = context.Model.FindEntityType(typeof(TenantOffsetEntity))
            ?? throw new InvalidOperationException("The tenant offset entity is not mapped.");
        var table = QuoteIdentifier(entityType.GetTableName()
            ?? throw new InvalidOperationException("The tenant offset table is not mapped."));
        var schema = entityType.GetSchema();
        var qualifiedTable = schema is null ? table : $"{QuoteIdentifier(schema)}.{table}";

        var sql = "INSERT INTO " + qualifiedTable +
            " (\"TenantId\", \"NextOffset\") VALUES ({0}, 0) ON CONFLICT (\"TenantId\") DO NOTHING";
        await context.Database.ExecuteSqlRawAsync(
            sql,
            [tenantId],
            cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE " + qualifiedTable + " SET \"NextOffset\" = \"NextOffset\" WHERE \"TenantId\" = {0}",
            [tenantId],
            cancellationToken);

        return await context.TenantOffsets
            .SingleAsync(value => value.TenantId == tenantId, cancellationToken);
    }

    private static string QuoteIdentifier(string identifier) =>
        $@"""{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}""";


    /// <summary>
    /// Reads all events in a stream in stream-version order.
    /// </summary>
    public async Task<IReadOnlyList<EventEnvelope>> ReadStreamAsync(
        string tenantId,
        string streamId,
        long? fromVersion = null,
        long? toVersion = null,
        CancellationToken cancellationToken = default)
    {
        tenantId = ResolveTenant(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        if (fromVersion is < 1 || toVersion is < 1 || (fromVersion.HasValue && toVersion.HasValue && fromVersion > toVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion), "Stream version bounds must be positive and ordered.");
        }

        var query = context.Events
            .Where(value => value.TenantId == tenantId && value.StreamId == streamId)
            .AsQueryable();
        if (fromVersion.HasValue)
        {
            query = query.Where(value => value.StreamVersion >= fromVersion.Value);
        }

        if (toVersion.HasValue)
        {
            query = query.Where(value => value.StreamVersion <= toVersion.Value);
        }

        var entities = await query.OrderBy(value => value.StreamVersion).ToListAsync(cancellationToken);
        return entities.Select(ToEnvelope).ToArray();
    }

    /// <summary>
    /// Reads committed events for a tenant by tenant offset.
    /// </summary>
    public async Task<IReadOnlyList<EventEnvelope>> ReadTenantOffsetsAsync(
        string tenantId,
        long afterOffset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        tenantId = ResolveTenant(tenantId);
        return await ReadTenantOffsetsCoreAsync(tenantId, afterOffset, limit, cancellationToken);
    }

    /// <summary>
    /// Reads committed events for a tenant from an explicit background-worker tenant scope.
    /// </summary>
    /// <remarks>
    /// This method is intended for registered EventLoom workers and administrative
    /// operations that do not execute in an application request scope.
    /// </remarks>
    public Task<IReadOnlyList<EventEnvelope>> ReadTenantOffsetsForBackgroundAsync(
        string tenantId,
        long afterOffset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        ReadTenantOffsetsCoreAsync(new TenantId(tenantId).Value, afterOffset, limit, cancellationToken);

    private async Task<IReadOnlyList<EventEnvelope>> ReadTenantOffsetsCoreAsync(
        string tenantId,
        long afterOffset,
        int limit,
        CancellationToken cancellationToken)
    {
        if (afterOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterOffset));
        }

        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Tenant offset read limits must be between 1 and 10,000.");
        }

        var entities = await context.Events
            .Where(value => value.TenantId == tenantId && value.TenantOffset > afterOffset)
            .OrderBy(value => value.TenantOffset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return entities.Select(ToEnvelope).ToArray();
    }

    private string ResolveTenant(string requestedTenantId)
    {
        var requested = new TenantId(requestedTenantId);
        if (eventStoreOptions.TenancyMode != TenancyMode.Required)
        {
            return requested.Value;
        }

        var current = tenantAccessor?.TenantId
            ?? throw new InvalidOperationException(
                "Tenancy is required, but the scoped tenant accessor did not provide a tenant.");
        if (current.Value != requested.Value)
        {
            throw new InvalidOperationException(
                $"The requested tenant '{requested.Value}' does not match the scoped tenant '{current.Value}'.");
        }

        return current.Value;
    }

    private EventEnvelope ToEnvelope(EventEntity entity) =>
        ToEnvelope(entity, serializer.Deserialize(entity.EventType, entity.EventTypeVersion, entity.Payload),
            CreateMetadata(entity));

    private static EventMetadata CreateMetadata(EventEntity entity) =>
        entity.Headers is null
            ? new EventMetadata(entity.CorrelationId, entity.CausationId, entity.Actor)
            : new EventMetadata(
                entity.CorrelationId,
                entity.CausationId,
                entity.Actor,
                JsonSerializer.Deserialize<Dictionary<string, string>>(entity.Headers)
                    ?? throw new InvalidOperationException($"Event '{entity.EventId}' has invalid metadata headers."));

    private static EventEnvelope ToEnvelope(EventEntity entity, IDomainEvent @event, EventMetadata metadata) =>
        new(
            entity.EventId,
            entity.EventType,
            entity.EventTypeVersion,
            entity.StreamId,
            entity.AggregateType,
            entity.StreamVersion,
            entity.TenantOffset,
            new TenantId(entity.TenantId),
            entity.OccurredAt,
            @event,
            metadata);
}

/// <summary>Describes an atomic event append.</summary>
public sealed record AppendRequest(
    string TenantId,
    string StreamId,
    string AggregateType,
    ExpectedVersion ExpectedVersion,
    IReadOnlyList<IDomainEvent> Events,
    EventMetadata Metadata,
    string? AppendId = null);

/// <summary>Describes the result of an event append.</summary>
public sealed record AppendResult(IReadOnlyList<EventEnvelope> Events, bool WasIdempotentReplay);

/// <summary>Indicates that the current stream version did not satisfy the append expectation.</summary>
public sealed class WrongExpectedVersionException(ExpectedVersion expected, long? actual)
    : InvalidOperationException($"Expected stream version '{expected}', but actual version was '{actual?.ToString() ?? "no stream"}'.");

/// <summary>Indicates that a concurrent append conflicted with the event-store database boundary.</summary>
public sealed class EventStoreConcurrencyException : InvalidOperationException
{
    /// <summary>Initializes a concurrency exception for a tenant and stream.</summary>
    public EventStoreConcurrencyException(string tenantId, string streamId, Exception innerException)
        : base($"A concurrent append conflicted for tenant '{tenantId}' and stream '{streamId}'.", innerException)
    {
    }
}
