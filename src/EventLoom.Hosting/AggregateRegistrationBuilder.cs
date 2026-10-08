namespace EventLoom.Hosting;

/// <summary>Configures persistence for one aggregate type.</summary>
public sealed class AggregateRegistrationBuilder<TAggregate, TId>
    where TAggregate : Aggregate<TAggregate, TId>
{
    private Func<TId, TAggregate>? factory;
    private string? aggregateType;
    private Func<TId, string>? streamId;
    private AggregateSnapshotConfiguration<TAggregate>? snapshotConfiguration;

    /// <summary>Configures how EventLoom creates an aggregate for replay.</summary>
    public AggregateRegistrationBuilder<TAggregate, TId> ConstructWith(Func<TId, TAggregate> factory)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>Configures the stable aggregate type and stream identifier.</summary>
    public AggregateRegistrationBuilder<TAggregate, TId> UseStream(
        string aggregateType,
        Func<TId, string> streamId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        this.aggregateType = aggregateType;
        this.streamId = streamId ?? throw new ArgumentNullException(nameof(streamId));
        return this;
    }

    /// <summary>Enables snapshots for the aggregate using one of its snapshot contracts.</summary>
    /// <remarks>
    /// <typeparamref name="TSnapshot"/> must implement <see cref="IAggregateSnapshot{TSelf, TAggregate}"/> for this
    /// aggregate, which also requires the aggregate to implement <see cref="ISnapshotable{TSnapshot}"/>.
    /// </remarks>
    /// <typeparam name="TSnapshot">The snapshot DTO type.</typeparam>
    /// <param name="configure">Configures snapshot cadence, retention, invalidation, and upcasters.</param>
    /// <returns>This builder.</returns>
    public AggregateRegistrationBuilder<TAggregate, TId> UseSnapshots<TSnapshot>(
        Action<AggregateSnapshotBuilder<TAggregate, TSnapshot>> configure)
        where TSnapshot : IAggregateSnapshot<TAggregate>
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AggregateSnapshotBuilder<TAggregate, TSnapshot>();
        configure(builder);
        snapshotConfiguration = builder.Build(static upcasters =>
            AggregateSnapshotDispatcher.Create<TAggregate, TId, TSnapshot>(upcasters));
        return this;
    }

    /// <summary>Enables snapshots for the aggregate using default snapshot settings.</summary>
    /// <typeparam name="TSnapshot">The snapshot DTO type.</typeparam>
    /// <returns>This builder.</returns>
    public AggregateRegistrationBuilder<TAggregate, TId> UseSnapshots<TSnapshot>()
        where TSnapshot : IAggregateSnapshot<TAggregate> =>
        UseSnapshots<TSnapshot>(static _ => { });

    internal AggregateRegistration<TAggregate, TId> Build()
    {
        if (factory is null)
        {
            throw new InvalidOperationException(
                $"Aggregate '{typeof(TAggregate).FullName}' requires ConstructWith(...).");
        }

        if (aggregateType is null || streamId is null)
        {
            throw new InvalidOperationException(
                $"Aggregate '{typeof(TAggregate).FullName}' requires UseStream(...).");
        }

        return new AggregateRegistration<TAggregate, TId>(
            factory,
            aggregateType,
            streamId,
            snapshotConfiguration);
    }
}

internal sealed record AggregateRegistration<TAggregate, TId>(
    Func<TId, TAggregate> Factory,
    string AggregateType,
    Func<TId, string> StreamId,
    AggregateSnapshotConfiguration<TAggregate>? SnapshotConfiguration)
    where TAggregate : Aggregate<TAggregate, TId>;
