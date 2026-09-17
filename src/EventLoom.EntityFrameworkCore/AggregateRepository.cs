using EventLoom;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Loads and saves aggregates through the EventLoom event store.</summary>
public sealed class AggregateRepository<TAggregate, TId>
    where TAggregate : Aggregate<TId>
{
    private readonly EventStore store;
    private readonly Func<TId, TAggregate> factory;
    private readonly Func<TAggregate, IEnumerable<IDomainEvent>> pendingEvents;
    private readonly Func<TAggregate, long> version;
    private readonly string? aggregateType;
    private readonly Func<TId, string>? streamId;
    private readonly ITenantAccessor? tenantAccessor;

    /// <summary>Initializes a repository with explicit persistence delegates.</summary>
    public AggregateRepository(
        EventStore store,
        Func<TId, TAggregate> factory,
        Func<TAggregate, IEnumerable<IDomainEvent>> pendingEvents,
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
    public AggregateRepository(
        EventStore store,
        Func<TId, TAggregate> factory,
        string aggregateType,
        Func<TId, string> streamId,
        ITenantAccessor? tenantAccessor = null)
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
    }

    /// <summary>Loads an aggregate from its complete stream history, or creates a new instance when absent.</summary>
    public async Task<TAggregate> LoadAsync(
        string tenantId,
        string streamId,
        TId id,
        CancellationToken cancellationToken = default)
    {
        var aggregate = factory(id);
        var history = await store.ReadStreamAsync(tenantId, streamId, cancellationToken: cancellationToken);
        if (history.Count > 0)
        {
            aggregate.ApplyHistory(history.Select(value => value.Event));
        }

        return aggregate;
    }

    /// <summary>
    /// Loads an aggregate using the configured stream identity and scoped tenant.
    /// </summary>
    public Task<TAggregate> LoadAsync(TId id, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        return LoadAsync(ResolveTenant(), streamId!(id), id, cancellationToken);
    }

    /// <summary>Saves pending aggregate events using the aggregate's current version as the expectation.</summary>
    public async Task<AppendResult> SaveAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        TAggregate aggregate,
        EventMetadata metadata,
        string? appendId = null,
        CancellationToken cancellationToken = default)
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
    public Task<AppendResult> SaveAsync(
        TAggregate aggregate,
        EventMetadata? metadata = null,
        string? appendId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        ArgumentNullException.ThrowIfNull(aggregate);
        return SaveAsync(
            ResolveTenant(),
            streamId!(aggregate.Id),
            aggregateType!,
            aggregate,
            metadata ?? new EventMetadata(),
            appendId,
            cancellationToken);
    }

    private void EnsureConfigured()
    {
        if (aggregateType is null || streamId is null)
        {
            throw new InvalidOperationException(
                "This repository was created with explicit persistence delegates. Register aggregate and stream identity to use the short operations.");
        }
    }

    private string ResolveTenant() =>
        tenantAccessor?.TenantId?.Value
        ?? throw new InvalidOperationException(
            "The short aggregate repository operation requires a tenant from the scoped ITenantAccessor.");
}
