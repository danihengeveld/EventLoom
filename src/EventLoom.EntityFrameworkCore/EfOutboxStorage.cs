using System.Data;
using EventLoom.Storage;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Reads durable outbox messages and records delivery outcomes through an EF Core context.</summary>
internal sealed class EfOutboxStorage(
    EventStoreDbContext context,
    EfStorageDialect dialect,
    TimeProvider timeProvider) : IOutboxStorage
{
    public async Task<IReadOnlyList<string>> ReadPendingTenantIdsAsync(CancellationToken cancellationToken = default) =>
        await context.Outbox.AsNoTracking()
            .Where(value => value.PublishedAt == null)
            .Select(value => value.TenantId)
            .Distinct()
            .OrderBy(value => value)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default) =>
        await context.Outbox.AsNoTracking()
            .CountAsync(value => value.PublishedAt == null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<OutboxMessage>> ReadPendingAsync(
        string tenantId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var messages = await context.Outbox.AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.PublishedAt == null)
            .OrderBy(value => value.TenantOffset)
            .Take(limit)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return messages.Select(ToMessage).ToArray();
    }

    public async Task<OutboxMessage?> GetAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var message = await context.Outbox.AsNoTracking().SingleOrDefaultAsync(
            value => value.TenantId == tenantId && value.MessageId == messageId,
            cancellationToken).ConfigureAwait(false);
        return message is null ? null : ToMessage(message);
    }

    public async Task<IReadOnlyList<OutboxAttempt>> ReadAttemptsAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        var attempts = await context.OutboxAttempts.AsNoTracking()
            .Where(value => value.TenantId == tenantId && value.MessageId == messageId)
            .OrderBy(value => value.AttemptNumber)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return attempts.Select(value => new OutboxAttempt(
            value.MessageId,
            value.TenantId,
            value.AttemptNumber,
            value.AttemptedAt,
            value.Succeeded,
            value.ExceptionType)).ToArray();
    }

    public async Task<bool> RecordAttemptAsync(
        OutboxMessage message,
        WorkerLease lease,
        string? exceptionType,
        TimeSpan successfulDeliveryRetention,
        CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await VerifyLeaseAsync(message.TenantId, lease, cancellationToken).ConfigureAwait(false);
        var entity = await context.Outbox.SingleOrDefaultAsync(
            value => value.MessageId == message.MessageId,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException(
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
        if (exceptionType is null && successfulDeliveryRetention == TimeSpan.Zero)
        {
            await context.OutboxAttempts
                .Where(value => value.MessageId == entity.MessageId)
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            context.Outbox.Remove(entity);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        context.OutboxAttempts.Add(new OutboxAttemptEntity
        {
            MessageId = entity.MessageId,
            TenantId = entity.TenantId,
            AttemptNumber = entity.AttemptCount,
            AttemptedAt = attemptedAt,
            Succeeded = exceptionType is null,
            ExceptionType = exceptionType
        });
        if (exceptionType is null)
        {
            entity.PublishedAt = attemptedAt;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> PurgePublishedAsync(
        TimeSpan successfulDeliveryRetention,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var maximumAge = now - DateTimeOffset.MinValue;
        var cutoff = successfulDeliveryRetention >= maximumAge
            ? DateTimeOffset.MinValue
            : now - successfulDeliveryRetention;

        context.ChangeTracker.Clear();
        Guid[] messageIds;
        if (dialect.FiltersPublishedOutboxInDatabase)
        {
            messageIds = await context.Outbox.AsNoTracking()
                .Where(value => value.PublishedAt != null && value.PublishedAt <= cutoff)
                .OrderBy(value => value.PublishedAt)
                .ThenBy(value => value.Id)
                .Select(value => value.MessageId)
                .Take(limit)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var candidates = await context.Outbox.AsNoTracking()
                .Where(value => value.PublishedAt != null)
                .OrderBy(value => value.Id)
                .Select(value => new { value.MessageId, value.PublishedAt })
                .Take(limit)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            messageIds = candidates
                .Where(value => value.PublishedAt <= cutoff)
                .Select(value => value.MessageId)
                .ToArray();
        }

        if (messageIds.Length == 0)
        {
            return 0;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await context.OutboxAttempts
            .Where(value => messageIds.Contains(value.MessageId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await context.Outbox
            .Where(value => messageIds.Contains(value.MessageId) && value.PublishedAt != null)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    private async Task VerifyLeaseAsync(string tenantId, WorkerLease lease, CancellationToken cancellationToken)
    {
        var active = await context.ProjectionLeases.AsNoTracking().SingleOrDefaultAsync(
            value =>
                value.TenantId == tenantId &&
                value.LeaseName == lease.LeaseName &&
                value.OwnerId == lease.OwnerId &&
                value.FencingToken == lease.FencingToken,
            cancellationToken).ConfigureAwait(false);
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
            EfEventStorage.CreateMetadata(
                entity.MessageId,
                entity.CorrelationId,
                entity.CausationId,
                entity.Actor,
                entity.Headers),
            entity.AttemptCount,
            entity.PublishedAt);
}
