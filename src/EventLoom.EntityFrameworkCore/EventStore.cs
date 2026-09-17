using Microsoft.EntityFrameworkCore;
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
    IEventStoreRetryPolicy? retryPolicy = null)
{
    private readonly EventStoreDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly EventSerializer serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IEventIdGenerator eventIdGenerator =
        eventIdGenerator ?? throw new ArgumentNullException(nameof(eventIdGenerator));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly EventStoreOptions eventStoreOptions = eventStoreOptions ?? new();
    private readonly ITenantAccessor? tenantAccessor = tenantAccessor;
    private readonly IEventStoreRetryPolicy retryPolicy = retryPolicy ?? new NoopEventStoreRetryPolicy();

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
                cancellationToken => AppendCoreAsync(request, tenantId, cancellationToken),
                cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new EventStoreConcurrencyException(tenantId, request.StreamId, exception);
        }
    }

    private async Task<AppendResult> AppendCoreAsync(
        AppendRequest request,
        string tenantId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
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
                await transaction.CommitAsync(cancellationToken);
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

        var position = await context.TenantPositions
            .SingleOrDefaultAsync(value => value.TenantId == tenantId, cancellationToken);
        position ??= new TenantPositionEntity { TenantId = tenantId, NextPosition = 0 };
        if (context.Entry(position).State == EntityState.Detached)
        {
            context.TenantPositions.Add(position);
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
                GlobalPosition = ++position.NextPosition,
                EventType = payload.EventName,
                EventTypeVersion = payload.Version,
                Payload = payload.Payload,
                OccurredAt = timeProvider.GetUtcNow(),
                CorrelationId = request.Metadata.CorrelationId,
                CausationId = request.Metadata.CausationId,
                Actor = request.Metadata.Actor,
                AppendId = request.AppendId
            };
            context.Events.Add(eventEntity);
            envelopes.Add(ToEnvelope(eventEntity, @event, request.Metadata));
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
        await transaction.CommitAsync(cancellationToken);
        return new AppendResult(envelopes, false);
    }

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
    /// Reads committed events for a tenant by global position.
    /// </summary>
    public async Task<IReadOnlyList<EventEnvelope>> ReadPositionsAsync(
        string tenantId,
        long afterPosition = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        tenantId = ResolveTenant(tenantId);
        if (afterPosition < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterPosition));
        }

        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Position read limits must be between 1 and 10,000.");
        }

        var entities = await context.Events
            .Where(value => value.TenantId == tenantId && value.GlobalPosition > afterPosition)
            .OrderBy(value => value.GlobalPosition)
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
            new EventMetadata(entity.CorrelationId, entity.CausationId, entity.Actor));

    private static EventEnvelope ToEnvelope(EventEntity entity, IDomainEvent @event, EventMetadata metadata) =>
        new(
            entity.EventId,
            entity.EventType,
            entity.EventTypeVersion,
            entity.StreamId,
            entity.AggregateType,
            entity.StreamVersion,
            entity.GlobalPosition,
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
