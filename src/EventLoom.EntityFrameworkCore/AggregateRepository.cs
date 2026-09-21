using System.Diagnostics;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Loads and saves aggregates through the EventLoom event store.</summary>
public sealed class AggregateRepository<TAggregate, TId>
    where TAggregate : Aggregate<TId>
{
    private readonly EventStore store;
    private readonly Func<TId, TAggregate> factory;
    private readonly Func<TAggregate, IEnumerable<object>> pendingEvents;
    private readonly Func<TAggregate, long> version;
    private readonly string? aggregateType;
    private readonly Func<TId, string>? streamId;
    private readonly ITenantAccessor? tenantAccessor;
    private readonly SnapshotStore? snapshotStore;
    private readonly AggregateSnapshotDispatcher<TAggregate>? snapshotDispatcher;
    private readonly ISnapshotPolicy? snapshotPolicy;
    private readonly ISnapshotInvalidator? snapshotInvalidator;
    private readonly ISnapshotRetentionPolicy? snapshotRetentionPolicy;

    /// <summary>Initializes a repository with explicit persistence delegates.</summary>
    public AggregateRepository(
        EventStore store,
        Func<TId, TAggregate> factory,
        Func<TAggregate, IEnumerable<object>> pendingEvents,
        Func<TAggregate, long> version)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.pendingEvents = pendingEvents ?? throw new ArgumentNullException(nameof(pendingEvents));
        this.version = version ?? throw new ArgumentNullException(nameof(version));
    }

    /// <summary>
    /// Initializes a repository with configured aggregate and stream identity.
    /// </summary>
    internal AggregateRepository(
        EventStore store,
        Func<TId, TAggregate> factory,
        string aggregateType,
        Func<TId, string> streamId,
        ITenantAccessor? tenantAccessor = null,
        SnapshotStore? snapshotStore = null,
        Type? snapshotType = null,
        ISnapshotPolicy? snapshotPolicy = null,
        ISnapshotInvalidator? snapshotInvalidator = null,
        ISnapshotRetentionPolicy? snapshotRetentionPolicy = null,
        IEnumerable<ISnapshotUpcaster>? snapshotUpcasters = null)
        : this(
            store,
            factory,
            aggregate => aggregate.PendingEvents.Select(value => value.Event),
            aggregate => aggregate.Version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        this.aggregateType = aggregateType;
        this.streamId = streamId ?? throw new ArgumentNullException(nameof(streamId));
        this.tenantAccessor = tenantAccessor;
        if (snapshotType is not null && snapshotStore is null)
        {
            throw new ArgumentException("A snapshot store is required when a snapshot type is configured.",
                nameof(snapshotStore));
        }

        this.snapshotStore = snapshotStore;
        snapshotDispatcher = snapshotType is null
            ? null
            : new AggregateSnapshotDispatcher<TAggregate>(snapshotType, snapshotUpcasters);
        this.snapshotPolicy = snapshotDispatcher is null
            ? null
            : snapshotPolicy ?? new EveryNEventsSnapshotPolicy(100);
        this.snapshotInvalidator = snapshotInvalidator;
        this.snapshotRetentionPolicy = snapshotRetentionPolicy;
    }

    /// <summary>Loads an aggregate from its complete stream history, or creates a new instance when absent.</summary>
    public Task<TAggregate> LoadAsync(
        string tenantId,
        string streamId,
        TId id) =>
        LoadAsync(tenantId, streamId, id, CancellationToken.None);

    /// <summary>Loads an aggregate from its complete stream history, or creates a new instance when absent.</summary>
    public async Task<TAggregate> LoadAsync(
        string tenantId,
        string streamId,
        TId id,
        CancellationToken cancellationToken)
    {
        var aggregate = factory(id);
        using var activity = EventLoomTelemetry.ActivitySource.StartActivity(
            "eventloom.aggregate.load",
            ActivityKind.Client);
        activity?.SetTag("eventloom.aggregate.type", aggregateType ?? "unconfigured");
        var startedAt = TimeProvider.System.GetTimestamp();
        IReadOnlyList<EventEnvelope> history;
        using (var readActivity = EventLoomTelemetry.ActivitySource.StartActivity(
                   "eventloom.event-stream.read",
                   ActivityKind.Client))
        {
            history = await store.ReadStreamAsync(tenantId, streamId, cancellationToken: cancellationToken);
            readActivity?.SetTag("eventloom.event.count", history.Count);
        }

        if (history.Count > 0)
        {
            using var replayActivity = EventLoomTelemetry.ActivitySource.StartActivity(
                "eventloom.aggregate.replay",
                ActivityKind.Internal);
            replayActivity?.SetTag("eventloom.replay.tail_event_count", history.Count);
            aggregate.ApplyHistory(history.Select(value => value.Event));
        }

        RecordLoad(aggregateType ?? "unconfigured", history.Count, startedAt, activity);
        return aggregate;
    }

    /// <summary>
    /// Loads an aggregate using the configured stream identity and scoped tenant.
    /// </summary>
    public Task<TAggregate> LoadAsync(TId id) =>
        LoadAsync(id, CancellationToken.None);

    /// <summary>
    /// Loads an aggregate using the configured stream identity and scoped tenant.
    /// </summary>
    public async Task<TAggregate> LoadAsync(TId id, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var tenantId = ResolveTenant();
        var aggregate = factory(id);
        using var activity = EventLoomTelemetry.ActivitySource.StartActivity(
            "eventloom.aggregate.load",
            ActivityKind.Client);
        activity?.SetTag("eventloom.aggregate.type", aggregateType);
        var startedAt = TimeProvider.System.GetTimestamp();
        long? fromVersion = null;
        var snapshotUsed = false;
        if (snapshotStore is not null && snapshotDispatcher is not null)
        {
            SnapshotEnvelope? snapshot;
            using (var snapshotActivity = EventLoomTelemetry.ActivitySource.StartActivity(
                       "eventloom.snapshot.read",
                       ActivityKind.Client))
            {
                snapshotActivity?.SetTag("eventloom.snapshot.type", snapshotDispatcher.SnapshotType);
                snapshot = await snapshotStore.ReadLatestAsync(
                    tenantId,
                    streamId!(id),
                    aggregateType!,
                    snapshotDispatcher.SnapshotType,
                    cancellationToken);
            }

            if (snapshot is not null)
            {
                try
                {
                    snapshotDispatcher.Restore(aggregate, snapshot.SchemaVersion, snapshot.Payload);
                    aggregate.RestoreSnapshotVersion(snapshot.StreamVersion);
                    fromVersion = snapshot.StreamVersion + 1;
                    snapshotUsed = true;
                }
                catch (SnapshotDeserializationException)
                {
                    await InvalidateSnapshotAsync(snapshot, SnapshotInvalidationReason.Corrupt, cancellationToken);
                    aggregate = factory(id);
                }
                catch (SnapshotIncompatibleException)
                {
                    await InvalidateSnapshotAsync(snapshot, SnapshotInvalidationReason.Incompatible, cancellationToken);
                    aggregate = factory(id);
                }
                catch (SnapshotUpcastException)
                {
                    await InvalidateSnapshotAsync(snapshot, SnapshotInvalidationReason.UpcastFailed, cancellationToken);
                    aggregate = factory(id);
                }
            }
        }

        IReadOnlyList<EventEnvelope> history;
        using (var readActivity = EventLoomTelemetry.ActivitySource.StartActivity(
                   "eventloom.event-stream.read",
                   ActivityKind.Client))
        {
            history = await store.ReadStreamAsync(
                tenantId,
                streamId!(id),
                fromVersion,
                cancellationToken: cancellationToken);
            readActivity?.SetTag("eventloom.event.count", history.Count);
        }

        using (var replayActivity = EventLoomTelemetry.ActivitySource.StartActivity(
                   "eventloom.aggregate.replay",
                   ActivityKind.Internal))
        {
            replayActivity?.SetTag("eventloom.replay.tail_event_count", history.Count);
            replayActivity?.SetTag("eventloom.snapshot.used", snapshotUsed);
            aggregate.ApplyHistory(history.Select(value => value.Event));
        }

        activity?.SetTag("eventloom.snapshot.used", snapshotUsed);
        RecordLoad(aggregateType!, history.Count, startedAt, activity);
        return aggregate;
    }

    /// <summary>Saves pending aggregate events using the aggregate's current version as the expectation.</summary>
    public Task<AppendResult> SaveAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        TAggregate aggregate,
        EventMetadata metadata) =>
        SaveAsync(tenantId, streamId, aggregateType, aggregate, metadata, null, CancellationToken.None);

    /// <summary>Saves pending aggregate events using the aggregate's current version as the expectation.</summary>
    public Task<AppendResult> SaveAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        TAggregate aggregate,
        EventMetadata metadata,
        string? appendId) =>
        SaveAsync(tenantId, streamId, aggregateType, aggregate, metadata, appendId, CancellationToken.None);

    /// <summary>Saves pending aggregate events using the aggregate's current version as the expectation.</summary>
    public async Task<AppendResult> SaveAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        TAggregate aggregate,
        EventMetadata metadata,
        string? appendId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var events = pendingEvents(aggregate).ToArray();
        if (events.Length == 0)
        {
            return new AppendResult([], false);
        }

        var expectedVersion = version(aggregate) - events.Length;
        var result = await store.AppendAsync(
            new AppendRequest(
                tenantId,
                streamId,
                aggregateType,
                expectedVersion == 0 ? ExpectedVersion.NoStream : ExpectedVersion.Exact(expectedVersion),
                events,
                metadata,
                appendId),
            cancellationToken);
        if (!result.WasIdempotentReplay)
        {
            aggregate.ClearPendingEvents();
        }

        return result;
    }

    /// <summary>
    /// Saves pending events using configured identity, scoped tenant, and empty metadata by default.
    /// </summary>
    public Task<AppendResult> SaveAsync(TAggregate aggregate) =>
        SaveAsync(aggregate, null, null, CancellationToken.None);

    /// <summary>
    /// Saves pending events using configured identity and scoped tenant.
    /// </summary>
    public Task<AppendResult> SaveAsync(
        TAggregate aggregate,
        EventMetadata? metadata) =>
        SaveAsync(aggregate, metadata, null, CancellationToken.None);

    /// <summary>
    /// Saves pending events using configured identity and scoped tenant.
    /// </summary>
    public Task<AppendResult> SaveAsync(
        TAggregate aggregate,
        EventMetadata? metadata,
        string? appendId) =>
        SaveAsync(aggregate, metadata, appendId, CancellationToken.None);

    /// <summary>
    /// Saves pending events using configured identity and scoped tenant.
    /// </summary>
    public async Task<AppendResult> SaveAsync(
        TAggregate aggregate,
        EventMetadata? metadata,
        string? appendId,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        ArgumentNullException.ThrowIfNull(aggregate);
        var result = await SaveAsync(
            ResolveTenant(),
            streamId!(aggregate.Id),
            aggregateType!,
            aggregate,
            metadata ?? new EventMetadata(),
            appendId,
            cancellationToken);
        if (!result.WasIdempotentReplay &&
            snapshotStore is not null &&
            snapshotDispatcher is not null &&
            snapshotPolicy!.ShouldSnapshot(aggregate.Version))
        {
            await snapshotStore.WriteWithRetentionAsync(
                new SnapshotWriteRequest(
                    ResolveTenant(),
                    streamId!(aggregate.Id),
                    aggregateType!,
                    aggregate.Version,
                    snapshotDispatcher.SnapshotType,
                    snapshotDispatcher.SchemaVersion,
                    snapshotDispatcher.Capture(aggregate)),
                snapshotRetentionPolicy,
                cancellationToken);
        }

        return result;
    }

    private static void RecordLoad(
        string aggregateType,
        int tailEventCount,
        long startedAt,
        Activity? activity)
    {
        var tags = new TagList
        {
            { "eventloom.aggregate.type", aggregateType }
        };
        EventLoomTelemetry.AggregateLoads.Add(1, tags);
        EventLoomTelemetry.ReplayedEvents.Add(tailEventCount, tags);
        EventLoomTelemetry.AggregateLoadDuration.Record(
            TimeProvider.System.GetElapsedTime(startedAt).TotalMilliseconds,
            tags);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    private void EnsureConfigured()
    {
        if (aggregateType is null || streamId is null)
        {
            throw new InvalidOperationException(
                "This repository was created with explicit persistence delegates. Register aggregate and stream identity to use the short operations.");
        }
    }

    private Task InvalidateSnapshotAsync(
        SnapshotEnvelope snapshot,
        SnapshotInvalidationReason reason,
        CancellationToken cancellationToken) =>
        snapshotInvalidator?.ShouldInvalidate(snapshot.SnapshotType, snapshot.SchemaVersion, reason) == true
            ? snapshotStore!.InvalidateAsync(snapshot, cancellationToken)
            : Task.CompletedTask;

    private string ResolveTenant() =>
        tenantAccessor?.TenantId?.Value
        ?? throw new InvalidOperationException(
            "The short aggregate repository operation requires a tenant from the scoped ITenantAccessor.");
}
