namespace EventLoom.Storage;

/// <summary>Describes an immutable integration message written with an event append.</summary>
public sealed record OutboxMessage(
    Guid MessageId,
    string TenantId,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    long TenantOffset,
    string EventType,
    int EventTypeVersion,
    string Payload,
    DateTimeOffset OccurredAt,
    EventMetadata Metadata,
    int AttemptCount,
    DateTimeOffset? PublishedAt);

/// <summary>Describes one completed attempt to publish an outbox message.</summary>
public sealed record OutboxAttempt(
    Guid MessageId,
    string TenantId,
    int AttemptNumber,
    DateTimeOffset AttemptedAt,
    bool Succeeded,
    string? ExceptionType);

/// <summary>Summarizes unpublished outbox work for operational health checks.</summary>
public sealed record OutboxHealthSummary(int PendingMessageCount);

/// <summary>Indicates that an outbox worker lost ownership before recording delivery.</summary>
public sealed class OutboxLeaseLostException(string tenantId)
    : InvalidOperationException($"The outbox publisher lost its lease for tenant '{tenantId}'.");

/// <summary>Validates outbox requests over a provider's outbox storage.</summary>
internal sealed class OutboxStore(IOutboxStorage storage)
{
    /// <summary>Gets the stable lease name used by tenant outbox publishers.</summary>
    public const string OutboxPublisherLeaseName = "outbox:publisher";

    private readonly IOutboxStorage storage = storage ?? throw new ArgumentNullException(nameof(storage));

    /// <summary>Lists tenants with unpublished outbox messages in deterministic order.</summary>
    public Task<IReadOnlyList<string>> ReadPendingTenantIdsAsync(CancellationToken cancellationToken = default) =>
        storage.ReadPendingTenantIdsAsync(cancellationToken);

    /// <summary>Gets the number of messages awaiting publication without returning message or event data.</summary>
    public async Task<OutboxHealthSummary> GetHealthSummaryAsync(CancellationToken cancellationToken = default) =>
        new(await storage.CountPendingAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Reads unpublished messages for a tenant in committed tenant-offset order.</summary>
    public Task<IReadOnlyList<OutboxMessage>> ReadPendingAsync(
        string tenantId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        tenantId = new TenantId(tenantId).Value;
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Outbox read limits must be between 1 and 10,000.");
        }

        return storage.ReadPendingAsync(tenantId, limit, cancellationToken);
    }

    /// <summary>Gets a durable outbox message by its tenant-scoped stable message identifier.</summary>
    public Task<OutboxMessage?> GetAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        storage.GetAsync(new TenantId(tenantId).Value, messageId, cancellationToken);

    /// <summary>Lists the persisted publication history for a message.</summary>
    public Task<IReadOnlyList<OutboxAttempt>> ReadAttemptsAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        storage.ReadAttemptsAsync(new TenantId(tenantId).Value, messageId, cancellationToken);

    /// <summary>
    /// Records a failed publication attempt or removes the message after successful publication.
    /// </summary>
    /// <returns><see langword="false"/> when another worker has already published the message.</returns>
    public Task<bool> RecordAttemptAsync(
        OutboxMessage message,
        WorkerLease lease,
        Exception? exception,
        CancellationToken cancellationToken = default) =>
        RecordAttemptAsync(message, lease, exception, TimeSpan.Zero, cancellationToken);

    internal Task<bool> RecordAttemptAsync(
        OutboxMessage message,
        WorkerLease lease,
        Exception? exception,
        TimeSpan successfulDeliveryRetention,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lease);
        if (successfulDeliveryRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(successfulDeliveryRetention));
        }

        if (message.TenantId != lease.TenantId)
        {
            throw new InvalidOperationException("The outbox message does not belong to the supplied lease tenant.");
        }

        return storage.RecordAttemptAsync(
            message,
            lease,
            exception is null ? null : exception.GetType().FullName ?? exception.GetType().Name,
            successfulDeliveryRetention,
            cancellationToken);
    }

    internal Task<int> PurgePublishedAsync(
        TimeSpan successfulDeliveryRetention,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (successfulDeliveryRetention < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(successfulDeliveryRetention));
        }

        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Outbox purge limits must be between 1 and 10,000.");
        }

        return storage.PurgePublishedAsync(successfulDeliveryRetention, limit, cancellationToken);
    }
}
