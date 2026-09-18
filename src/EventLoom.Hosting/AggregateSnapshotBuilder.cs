using System.Text.Json.Serialization.Metadata;

namespace EventLoom.Hosting;

/// <summary>Builds the snapshot configuration for one aggregate type.</summary>
public sealed class AggregateSnapshotBuilder<TAggregate>
{
    private IAggregateSnapshotAdapter<TAggregate>? adapter;
    private ISnapshotPolicy? policy;
    private ISnapshotRetentionPolicy? retentionPolicy;
    private ISnapshotInvalidator? invalidator;

    /// <summary>Uses an application-provided snapshot adapter.</summary>
    public AggregateSnapshotBuilder<TAggregate> UseAdapter(IAggregateSnapshotAdapter<TAggregate> value)
    {
        adapter = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Uses a typed JSON snapshot DTO and optional upcasters.</summary>
    public AggregateSnapshotBuilder<TAggregate> UseAdapter<TSnapshot>(
        Func<TAggregate, TSnapshot> capture,
        Action<TAggregate, TSnapshot> restore,
        JsonTypeInfo<TSnapshot>? jsonTypeInfo = null,
        IEnumerable<ISnapshotUpcaster>? upcasters = null)
        where TSnapshot : IAggregateSnapshot
    {
        adapter = new AggregateSnapshotAdapter<TAggregate, TSnapshot>(
            capture,
            restore,
            jsonTypeInfo,
            upcasters);
        return this;
    }

    /// <summary>Uses a custom snapshot cadence policy.</summary>
    public AggregateSnapshotBuilder<TAggregate> UsePolicy(ISnapshotPolicy value)
    {
        policy = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Captures a snapshot every <paramref name="interval"/> events.</summary>
    public AggregateSnapshotBuilder<TAggregate> Every(int interval) =>
        UsePolicy(new EveryNEventsSnapshotPolicy(interval));

    /// <summary>Uses a custom snapshot retention policy for this aggregate.</summary>
    public AggregateSnapshotBuilder<TAggregate> UseRetention(ISnapshotRetentionPolicy value)
    {
        retentionPolicy = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Keeps the latest <paramref name="count"/> snapshots for this aggregate.</summary>
    public AggregateSnapshotBuilder<TAggregate> KeepLatest(int count) =>
        UseRetention(new KeepLatestSnapshotsPolicy(count));

    /// <summary>Uses a policy to remove unusable snapshots after safe replay fallback.</summary>
    public AggregateSnapshotBuilder<TAggregate> UseInvalidator(ISnapshotInvalidator value)
    {
        invalidator = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    internal AggregateSnapshotConfiguration<TAggregate> Build() =>
        new(
            adapter ?? throw new InvalidOperationException(
                $"Aggregate '{typeof(TAggregate).FullName}' requires a snapshot adapter."),
            policy,
            retentionPolicy,
            invalidator);
}

internal sealed record AggregateSnapshotConfiguration<TAggregate>(
    IAggregateSnapshotAdapter<TAggregate> Adapter,
    ISnapshotPolicy? Policy,
    ISnapshotRetentionPolicy? RetentionPolicy,
    ISnapshotInvalidator? Invalidator);
