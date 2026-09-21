namespace EventLoom.Hosting;

/// <summary>Configures the projection worker: instance identity, polling, batching, lease, and retry behavior.</summary>
public sealed class EventStoreWorkerOptions
{
    /// <summary>Gets or sets the unique application-instance identity.</summary>
    public string InstanceId { get; set; } = WorkerIdentity.Create();

    /// <summary>Gets or sets the worker polling interval.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the maximum number of events processed in one batch.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Gets or sets the lease duration.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the lease renewal interval.</summary>
    public TimeSpan LeaseRenewalInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the maximum number of transient retry attempts.</summary>
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Validates the worker configuration.</summary>
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
    }
}
