using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventLoom.Storage;

/// <summary>
/// Provides transactional append and bounded read operations for an EventLoom event store, independent of the
/// storage provider that persists the events.
/// </summary>
public sealed class EventStore
{
    private readonly IEventStorage storage;
    private readonly EventSerializer serializer;
    private readonly IEventIdGenerator eventIdGenerator;
    private readonly TimeProvider timeProvider;
    private readonly EventStoreOptions eventStoreOptions;
    private readonly ITenantAccessor? tenantAccessor;
    private readonly IEventStoreRetryPolicy retryPolicy;
    private readonly IInlineProjectionDispatcher? inlineProjectionDispatcher;
    private readonly ILogger<EventStore> logger;

    internal EventStore(
        IEventStorage storage,
        EventSerializer serializer,
        IEventIdGenerator eventIdGenerator,
        TimeProvider timeProvider,
        EventStoreOptions? eventStoreOptions = null,
        ITenantAccessor? tenantAccessor = null,
        IEventStoreRetryPolicy? retryPolicy = null,
        IInlineProjectionDispatcher? inlineProjectionDispatcher = null,
        ILogger<EventStore>? logger = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        this.serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        this.eventIdGenerator = eventIdGenerator ?? throw new ArgumentNullException(nameof(eventIdGenerator));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.eventStoreOptions = eventStoreOptions ?? new EventStoreOptions();
        this.tenantAccessor = tenantAccessor;
        this.retryPolicy = retryPolicy ?? new NoopEventStoreRetryPolicy();
        this.inlineProjectionDispatcher = inlineProjectionDispatcher;
        this.logger = logger ?? NullLogger<EventStore>.Instance;
    }

    /// <summary>
    /// Appends a batch atomically and returns the persisted envelopes.
    /// </summary>
    /// <param name="request">The atomic append to persist.</param>
    /// <param name="cancellationToken">Cancels the append operation.</param>
    /// <returns>The persisted event envelopes.</returns>
    /// <exception cref="WrongExpectedVersionException">The stream version did not satisfy the expectation.</exception>
    /// <exception cref="EventStoreConcurrencyException">A concurrent append conflicted at the storage boundary.</exception>
    public async Task<AppendResult> AppendAsync(
        AppendRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var tenantId = ResolveTenant(request.TenantId);
        using var activity = EventLoomTelemetry.ActivitySource.StartActivity(
            "eventloom.append",
            ActivityKind.Producer);
        activity?.SetTag("eventloom.aggregate.type", request.AggregateType);
        activity?.SetTag("eventloom.event.count", request.Events.Count);
        var startedAt = timeProvider.GetTimestamp();
        try
        {
            var result = await retryPolicy.ExecuteAsync(
                token => AppendCoreAsync(request, tenantId, transaction: null, token),
                cancellationToken).ConfigureAwait(false);
            RecordAppendSuccess(request, result, startedAt, activity);
            return result;
        }
        catch (Exception exception)
        {
            RecordAppendFailure(request, startedAt, activity, exception);
            if (exception is EventStoreConcurrencyException or WrongExpectedVersionException ||
                !cancellationToken.IsCancellationRequested)
            {
                LogAppendFailure(request, exception);
            }

            throw;
        }
    }

    /// <summary>
    /// Begins a unit of work that shares one provider transaction between EventLoom appends and application
    /// state, so both commit or roll back together.
    /// </summary>
    /// <remarks>
    /// Use the provider's extension methods on the returned unit of work to join application state to the same
    /// transaction. The caller must commit or roll back the unit of work, then dispose it.
    /// </remarks>
    /// <param name="cancellationToken">Cancels beginning the transaction.</param>
    /// <returns>A unit of work scoped to a new provider transaction.</returns>
    /// <exception cref="NotSupportedException">The configured storage provider does not support shared transactions.</exception>
    public async Task<EventLoomUnitOfWork> BeginUnitOfWorkAsync(CancellationToken cancellationToken = default)
    {
        var transaction = await storage.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return new EventLoomUnitOfWork(this, transaction);
    }

    internal async Task<AppendResult> AppendWithinTransactionAsync(
        AppendRequest request,
        IStorageTransaction transaction,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var tenantId = ResolveTenant(request.TenantId);
        try
        {
            return await AppendCoreAsync(request, tenantId, transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is EventStoreConcurrencyException or WrongExpectedVersionException ||
            !cancellationToken.IsCancellationRequested)
        {
            LogAppendFailure(request, exception);
            throw;
        }
    }

    private async Task<AppendResult> AppendCoreAsync(
        AppendRequest request,
        string tenantId,
        IStorageTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var events = new NewStoredEvent[request.Events.Count];
        for (var index = 0; index < events.Length; index++)
        {
            var payload = serializer.SerializePayload(request.Events[index]);
            events[index] = new NewStoredEvent(
                eventIdGenerator.Create(),
                payload.EventName,
                payload.Version,
                payload.Payload,
                timeProvider.GetUtcNow());
        }

        var result = await storage.AppendAsync(
            new StorageAppendRequest(
                tenantId,
                request.StreamId,
                request.AggregateType,
                request.ExpectedVersion,
                events,
                request.Metadata,
                request.AppendId,
                eventStoreOptions.OutboxEnabled,
                inlineProjectionDispatcher is null ? null : DispatchInlineAsync),
            transaction,
            cancellationToken).ConfigureAwait(false);
        return result.WasIdempotentReplay
            ? new AppendResult(result.Events.Select(value => ToEnvelope(value)).ToArray(), true)
            : new AppendResult(
                result.Events.Select((stored, index) => ToEnvelope(stored, request.Events[index])).ToArray(),
                false);

        async Task DispatchInlineAsync(IReadOnlyList<StoredEvent> stored, CancellationToken token)
        {
            for (var index = 0; index < stored.Count; index++)
            {
                await inlineProjectionDispatcher!.DispatchAsync(ToEnvelope(stored[index], request.Events[index]), token)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void Validate(AppendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Events.Count == 0)
        {
            throw new ArgumentException("At least one event is required.", nameof(request));
        }
    }

    private void RecordAppendSuccess(
        AppendRequest request,
        AppendResult result,
        long startedAt,
        Activity? activity)
    {
        var tags = new TagList
        {
            { "eventloom.aggregate.type", request.AggregateType }
        };
        EventLoomTelemetry.Appends.Add(1, tags);
        EventLoomTelemetry.AppendedEvents.Add(result.Events.Count, tags);
        EventLoomTelemetry.AppendDuration.Record(timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, tags);
        activity?.SetTag("eventloom.append.idempotent_replay", result.WasIdempotentReplay);
        activity?.SetTag("eventloom.event.count", result.Events.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    private void RecordAppendFailure(
        AppendRequest request,
        long startedAt,
        Activity? activity,
        Exception exception)
    {
        var tags = new TagList
        {
            { "eventloom.aggregate.type", request.AggregateType },
            { "error.type", exception.GetType().FullName ?? exception.GetType().Name }
        };
        EventLoomTelemetry.AppendFailures.Add(1, tags);
        EventLoomTelemetry.AppendDuration.Record(timeProvider.GetElapsedTime(startedAt).TotalMilliseconds, tags);
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
    }

    private void LogAppendFailure(AppendRequest request, Exception exception)
    {
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        if (exception is EventStoreConcurrencyException or WrongExpectedVersionException)
        {
            logger.AppendRejected(request.AggregateType, exceptionType);
        }
        else
        {
            logger.AppendFailed(request.AggregateType, exceptionType);
        }
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
        if (fromVersion is < 1 || toVersion is < 1 ||
            (fromVersion.HasValue && toVersion.HasValue && fromVersion > toVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion),
                "Stream version bounds must be positive and ordered.");
        }

        var stored = await storage.ReadStreamAsync(tenantId, streamId, fromVersion, toVersion, cancellationToken)
            .ConfigureAwait(false);
        return stored.Select(value => ToEnvelope(value)).ToArray();
    }

    /// <summary>
    /// Reads committed events for a tenant by tenant offset.
    /// </summary>
    public Task<IReadOnlyList<EventEnvelope>> ReadTenantOffsetsAsync(
        string tenantId,
        long afterOffset = 0,
        int limit = 100,
        CancellationToken cancellationToken = default) =>
        ReadTenantOffsetsCoreAsync(ResolveTenant(tenantId), afterOffset, limit, cancellationToken);

    /// <summary>
    /// Reads committed events for a tenant from an explicit background-worker tenant scope.
    /// </summary>
    /// <remarks>
    /// This method is intended for registered EventLoom workers and administrative
    /// operations that do not execute in an application request scope.
    /// </remarks>
    internal Task<IReadOnlyList<EventEnvelope>> ReadTenantOffsetsForBackgroundAsync(
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
            throw new ArgumentOutOfRangeException(nameof(limit),
                "Tenant offset read limits must be between 1 and 10,000.");
        }

        var stored = await storage.ReadTenantAsync(tenantId, afterOffset, limit, cancellationToken)
            .ConfigureAwait(false);
        return stored.Select(value => ToEnvelope(value)).ToArray();
    }

    private string ResolveTenant(string requestedTenantId)
    {
        var requested = new TenantId(requestedTenantId);
        if (eventStoreOptions.TenancyMode == TenancyMode.SingleTenant)
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

    private EventEnvelope ToEnvelope(StoredEvent stored) =>
        ToEnvelope(stored, serializer.Deserialize(stored.EventType, stored.EventTypeVersion, stored.Payload));

    private static EventEnvelope ToEnvelope(StoredEvent stored, object @event) =>
        new(
            stored.EventId,
            stored.EventType,
            stored.EventTypeVersion,
            stored.StreamId,
            stored.AggregateType,
            stored.StreamVersion,
            stored.TenantOffset,
            new TenantId(stored.TenantId),
            stored.OccurredAt,
            @event,
            stored.Metadata);
}

/// <summary>Describes an atomic event append.</summary>
public sealed record AppendRequest(
    string TenantId,
    string StreamId,
    string AggregateType,
    ExpectedVersion ExpectedVersion,
    IReadOnlyList<object> Events,
    EventMetadata Metadata,
    string? AppendId = null);

/// <summary>Describes the result of an event append.</summary>
public sealed record AppendResult(IReadOnlyList<EventEnvelope> Events, bool WasIdempotentReplay);

/// <summary>Indicates that the current stream version did not satisfy the append expectation.</summary>
public sealed class WrongExpectedVersionException(ExpectedVersion expected, long? actual)
    : InvalidOperationException(
        $"Expected stream version '{expected}', but actual version was '{actual?.ToString() ?? "no stream"}'.");

/// <summary>Indicates that a concurrent append conflicted with the event-store storage boundary.</summary>
public sealed class EventStoreConcurrencyException : InvalidOperationException
{
    /// <summary>Initializes a concurrency exception for a tenant and stream.</summary>
    public EventStoreConcurrencyException(string tenantId, string streamId, Exception innerException)
        : base($"A concurrent append conflicted for tenant '{tenantId}' and stream '{streamId}'.", innerException)
    {
    }
}
