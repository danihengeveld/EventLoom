using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Provides transactional append and bounded read operations for an EventLoom event store.
/// </summary>
public sealed class EventStore(
    EventStoreDbContext context,
    EventSerializer serializer,
    IEventIdGenerator eventIdGenerator,
    TimeProvider timeProvider)
{
    private readonly EventStoreDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly EventSerializer serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    private readonly IEventIdGenerator eventIdGenerator =
        eventIdGenerator ?? throw new ArgumentNullException(nameof(eventIdGenerator));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var stream = await context.Streams
            .SingleOrDefaultAsync(
                value => value.TenantId == request.TenantId && value.StreamId == request.StreamId,
                cancellationToken);
        if (request.AppendId is not null)
        {
            var existing = await context.Events
                .Where(value => value.TenantId == request.TenantId && value.AppendId == request.AppendId)
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
            TenantId = request.TenantId,
            StreamId = request.StreamId,
            AggregateType = request.AggregateType,
            Version = 0
        };
        if (context.Entry(stream).State == EntityState.Detached)
        {
            context.Streams.Add(stream);
        }

        var position = await context.TenantPositions
            .SingleOrDefaultAsync(value => value.TenantId == request.TenantId, cancellationToken);
        position ??= new TenantPositionEntity { TenantId = request.TenantId, NextPosition = 0 };
        if (context.Entry(position).State == EntityState.Detached)
        {
            context.TenantPositions.Add(position);
        }

        var envelopes = new List<EventEnvelope>(request.Events.Count);
        foreach (var @event in request.Events)
        {
            var registration = serializer.SerializePayload(@event).EventName;
            var payload = serializer.SerializePayload(@event);
            var eventEntity = new EventEntity
            {
                EventId = eventIdGenerator.Create(),
                TenantId = request.TenantId,
                StreamId = request.StreamId,
                AggregateType = request.AggregateType,
                StreamVersion = ++stream.Version,
                GlobalPosition = ++position.NextPosition,
                EventType = registration,
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

        await context.SaveChangesAsync(cancellationToken);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
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
