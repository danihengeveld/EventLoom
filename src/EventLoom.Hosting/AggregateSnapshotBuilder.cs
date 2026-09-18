namespace EventLoom.Hosting;

/// <summary>Builds the snapshot configuration for one aggregate type.</summary>
public sealed class AggregateSnapshotBuilder<TAggregate, TSnapshot>
    where TAggregate : Aggregate
    where TSnapshot : IAggregateSnapshot<TAggregate>
{
    private ISnapshotPolicy? policy;
    private ISnapshotRetentionPolicy? retentionPolicy;
    private ISnapshotInvalidator? invalidator;
    private IReadOnlyList<ISnapshotUpcaster> upcasters = [];

    /// <summary>Uses a custom snapshot cadence policy.</summary>
    public AggregateSnapshotBuilder<TAggregate, TSnapshot> UsePolicy(ISnapshotPolicy value)
    {
        policy = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Captures a snapshot every <paramref name="interval"/> events.</summary>
    public AggregateSnapshotBuilder<TAggregate, TSnapshot> Every(int interval) =>
        UsePolicy(new EveryNEventsSnapshotPolicy(interval));

    /// <summary>Uses a custom snapshot retention policy for this aggregate.</summary>
    public AggregateSnapshotBuilder<TAggregate, TSnapshot> UseRetention(ISnapshotRetentionPolicy value)
    {
        retentionPolicy = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Keeps the latest <paramref name="count"/> snapshots for this aggregate.</summary>
    public AggregateSnapshotBuilder<TAggregate, TSnapshot> KeepLatest(int count) =>
        UseRetention(new KeepLatestSnapshotsPolicy(count));

    /// <summary>Uses a policy to remove unusable snapshots after safe replay fallback.</summary>
    public AggregateSnapshotBuilder<TAggregate, TSnapshot> UseInvalidator(ISnapshotInvalidator value)
    {
        invalidator = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Registers deterministic upcasters for earlier snapshot schemas.</summary>
    public AggregateSnapshotBuilder<TAggregate, TSnapshot> UseUpcasters(
        IEnumerable<ISnapshotUpcaster> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        upcasters = value.ToArray();
        return this;
    }

    internal AggregateSnapshotConfiguration<TAggregate> Build() =>
        new(
            typeof(TSnapshot),
            upcasters,
            policy,
            retentionPolicy,
            invalidator);
}

internal sealed record AggregateSnapshotConfiguration<TAggregate>(
    Type SnapshotType,
    IReadOnlyList<ISnapshotUpcaster> Upcasters,
    ISnapshotPolicy? Policy,
    ISnapshotRetentionPolicy? RetentionPolicy,
    ISnapshotInvalidator? Invalidator);
