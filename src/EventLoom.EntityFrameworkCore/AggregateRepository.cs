using EventLoom;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Loads and saves aggregates through the EventLoom event store.</summary>
public sealed class AggregateRepository<TAggregate, TId>(
    EventStore store,
    Func<TId, TAggregate> factory,
    Func<TAggregate, IEnumerable<IDomainEvent>> pendingEvents,
    Func<TAggregate, long> version)
    where TAggregate : Aggregate<TId>
{
    private readonly EventStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly Func<TId, TAggregate> factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly Func<TAggregate, IEnumerable<IDomainEvent>> pendingEvents =
        pendingEvents ?? throw new ArgumentNullException(nameof(pendingEvents));
    private readonly Func<TAggregate, long> version = version ?? throw new ArgumentNullException(nameof(version));

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
}
