using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

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

/// <summary>Reads durable outbox messages and records their delivery outcomes.</summary>
public sealed class OutboxStore(EventStoreDbContext context, TimeProvider timeProvider)
{
    private readonly EventStoreDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Lists tenants with unpublished outbox messages in deterministic order.</summary>
    public async Task<IReadOnlyList<string>> ReadPendingTenantIdsAsync(CancellationToken cancellationToken = default) =>
        await context.Outbox.AsNoTracking()
            .Where(value => value.PublishedAt == null)
            .Select(value => value.TenantId)
            .Distinct()
            .OrderBy(value => value)
            .ToArrayAsync(cancellationToken);

    /// <summary>
    /// Gets the number of messages awaiting publication without returning message or event data.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the query.</param>
    /// <returns>A payload-safe outbox health summary.</returns>
    public async Task<OutboxHealthSummary> GetHealthSummaryAsync(CancellationToken cancellationToken = default) =>
        new(await context.Outbox.AsNoTracking()
            .CountAsync(value => value.PublishedAt == null, cancellationToken));

    /// <summary>Reads unpublished messages for a tenant in committed tenant-offset order.</summary>
    public async Task<IReadOnlyList<OutboxMessage>> ReadPendingAsync(
        string tenantId,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        tenantId = new TenantId(tenantId).Value;
        if (limit is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Outbox read limits must be between 1 and 10,000.");
        }

        var messages = await context.Outbox.AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.PublishedAt == null)
            .OrderBy(value => value.TenantOffset)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        return messages.Select(ToMessage).ToArray();
    }

    /// <summary>Gets a durable outbox message by its tenant-scoped stable message identifier.</summary>
    public async Task<OutboxMessage?> GetAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        tenantId = new TenantId(tenantId).Value;
        var message = await context.Outbox.AsNoTracking().SingleOrDefaultAsync(
            value => value.TenantId == tenantId && value.MessageId == messageId,
            cancellationToken);
        return message is null ? null : ToMessage(message);
    }

    /// <summary>Lists the persisted publication history for a message.</summary>
    public async Task<IReadOnlyList<OutboxAttempt>> ReadAttemptsAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        tenantId = new TenantId(tenantId).Value;
        var attempts = await context.OutboxAttempts.AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.MessageId == messageId)
            .OrderBy(value => value.AttemptNumber)
            .ToArrayAsync(cancellationToken);
        return attempts.Select(value => new OutboxAttempt(
            value.MessageId,
            value.TenantId,
            value.AttemptNumber,
            value.AttemptedAt,
            value.Succeeded,
            value.ExceptionType)).ToArray();
    }

    /// <summary>
    /// Records a failed publication attempt or deletes the message and its attempts after successful publication.
    /// </summary>
    /// <returns><see langword="false"/> when another worker has already published the message.</returns>
    public async Task<bool> RecordAttemptAsync(
        OutboxMessage message,
        WorkerLease lease,
        Exception? exception,
        CancellationToken cancellationToken = default) =>
        await RecordAttemptAsync(
            message,
            lease,
            exception,
            TimeSpan.Zero,
            cancellationToken);

    internal async Task<bool> RecordAttemptAsync(
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

        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await VerifyLeaseAsync(message.TenantId, lease, cancellationToken);
        var entity = await context.Outbox.SingleOrDefaultAsync(
            value => value.MessageId == message.MessageId,
            cancellationToken) ?? throw new InvalidOperationException(
                $"Outbox message '{message.MessageId}' does not exist.");
        if (entity.TenantId != message.TenantId)
        {
            throw new InvalidOperationException("The outbox message tenant does not match the supplied message.");
        }

        if (entity.PublishedAt is not null)
        {
            return false;
        }

        var attemptedAt = timeProvider.GetUtcNow();
        entity.AttemptCount++;
        if (exception is null && successfulDeliveryRetention == TimeSpan.Zero)
        {
            await context.OutboxAttempts
                .Where(value => value.MessageId == entity.MessageId)
                .ExecuteDeleteAsync(cancellationToken);
            context.Outbox.Remove(entity);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        context.OutboxAttempts.Add(new OutboxAttemptEntity
        {
            MessageId = entity.MessageId,
            TenantId = entity.TenantId,
            AttemptNumber = entity.AttemptCount,
            AttemptedAt = attemptedAt,
            Succeeded = exception is null,
            ExceptionType = exception?.GetType().FullName ?? exception?.GetType().Name
        });
        if (exception is null)
        {
            entity.PublishedAt = attemptedAt;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal async Task<int> PurgePublishedAsync(
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

        var now = timeProvider.GetUtcNow();
        var maximumAge = now - DateTimeOffset.MinValue;
        var cutoff = successfulDeliveryRetention >= maximumAge
            ? DateTimeOffset.MinValue
            : now - successfulDeliveryRetention;

        context.ChangeTracker.Clear();
        Guid[] messageIds;
        if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            messageIds = await context.Outbox.AsNoTracking()
                .Where(value => value.PublishedAt != null && value.PublishedAt <= cutoff)
                .OrderBy(value => value.PublishedAt)
                .ThenBy(value => value.Id)
                .Select(value => value.MessageId)
                .Take(limit)
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            var candidates = await context.Outbox.AsNoTracking()
                .Where(value => value.PublishedAt != null)
                .OrderBy(value => value.Id)
                .Select(value => new { value.MessageId, value.PublishedAt })
                .Take(limit)
                .ToArrayAsync(cancellationToken);
            messageIds = candidates
                .Where(value => value.PublishedAt <= cutoff)
                .Select(value => value.MessageId)
                .ToArray();
        }

        if (messageIds.Length == 0)
        {
            return 0;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.OutboxAttempts
            .Where(value => messageIds.Contains(value.MessageId))
            .ExecuteDeleteAsync(cancellationToken);
        var deleted = await context.Outbox
            .Where(value => messageIds.Contains(value.MessageId) && value.PublishedAt != null)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    /// <summary>Gets the stable lease name used by tenant outbox publishers.</summary>
    public static string GetLeaseName() => "outbox:publisher";

    private async Task VerifyLeaseAsync(
        string tenantId,
        WorkerLease lease,
        CancellationToken cancellationToken)
    {
        var active = await context.ProjectionLeases.AsNoTracking().SingleOrDefaultAsync(
            value =>
                value.TenantId == tenantId &&
                value.LeaseName == GetLeaseName() &&
                value.OwnerId == lease.OwnerId &&
                value.FencingToken == lease.FencingToken,
            cancellationToken);
        if (active is null || active.LeaseUntil <= timeProvider.GetUtcNow())
        {
            throw new OutboxLeaseLostException(tenantId);
        }
    }

    private static OutboxMessage ToMessage(OutboxEntity entity) =>
        new(
            entity.MessageId,
            entity.TenantId,
            entity.StreamId,
            entity.AggregateType,
            entity.StreamVersion,
            entity.TenantOffset,
            entity.EventType,
            entity.EventTypeVersion,
            entity.Payload,
            entity.OccurredAt,
            CreateMetadata(entity),
            entity.AttemptCount,
            entity.PublishedAt);

    private static EventMetadata CreateMetadata(OutboxEntity entity) =>
        entity.Headers is null
            ? new EventMetadata(entity.CorrelationId, entity.CausationId, entity.Actor)
            : new EventMetadata(
                entity.CorrelationId,
                entity.CausationId,
                entity.Actor,
                JsonSerializer.Deserialize<Dictionary<string, string>>(entity.Headers)
                    ?? throw new InvalidOperationException(
                        $"Outbox message '{entity.MessageId}' has invalid metadata headers."));
}
