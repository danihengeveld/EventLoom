namespace EventLoom.Hosting;

/// <summary>
/// Configures the outbox publisher worker: its instance identity, polling, batching, lease,
/// retry, and successful-delivery retention behavior. Configure these through
/// <see cref="EventLoomBuilder.AddOutboxPublisher{TPublisher}(Action{OutboxOptions}?)"/>.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>Gets or sets the unique application-instance identity used to own outbox leases.</summary>
    public string InstanceId { get; set; } = WorkerIdentity.Create();

    /// <summary>Gets or sets the outbox worker polling interval.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the maximum number of outbox messages processed in one batch.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Gets or sets the outbox lease duration.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the outbox lease renewal interval.</summary>
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the maximum number of transient publish retry attempts.</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>
    /// Gets or sets how long successful messages and their attempt history are retained.
    /// The default is immediate deletion.
    /// </summary>
    public TimeSpan SuccessfulDeliveryRetention { get; set; } = TimeSpan.Zero;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InstanceId);
        if (PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval));
        }

        if (BatchSize is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize));
        }

        if (LeaseDuration <= TimeSpan.Zero || LeaseRenewalInterval <= TimeSpan.Zero ||
            LeaseRenewalInterval >= LeaseDuration)
        {
            throw new ArgumentException("Lease renewal must be positive and shorter than the lease duration.");
        }

        if (MaxRetryAttempts is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts));
        }

        if (SuccessfulDeliveryRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SuccessfulDeliveryRetention));
        }
    }
}
