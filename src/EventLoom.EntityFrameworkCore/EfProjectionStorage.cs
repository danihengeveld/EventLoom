using System.Data;
using EventLoom.Storage;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>The checkpoint transaction an EF Core transactional projection writes its read model in.</summary>
public sealed class EfProjectionTransaction : IProjectionTransactionContext
{
    internal EfProjectionTransaction(EventStoreDbContext context)
    {
        Context = context;
    }

    /// <summary>Gets the EventLoom context participating in the checkpoint transaction.</summary>
    public EventStoreDbContext Context { get; }
}

/// <summary>Persists checkpoints and failures and runs transactional deliveries through an EF Core context.</summary>
internal sealed class EfProjectionStorage(EventStoreDbContext context, TimeProvider timeProvider)
    : IProjectionStorage
{
    public async Task<ProjectionCheckpoint?> GetCheckpointAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await context.ProjectionCheckpoints.AsNoTracking().SingleOrDefaultAsync(
            value =>
                value.TenantId == tenantId &&
                value.ProjectionName == key.Name &&
                value.ProjectionVersion == key.Version,
            cancellationToken).ConfigureAwait(false);
        return checkpoint is null ? null : ToCheckpoint(checkpoint);
    }

    public async Task<IReadOnlyList<ProjectionCheckpoint>> ReadCheckpointsAsync(
        IReadOnlyCollection<ProjectionKey> keys,
        CancellationToken cancellationToken = default)
    {
        var keySet = keys.ToHashSet();
        var checkpoints = await context.ProjectionCheckpoints.AsNoTracking()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return checkpoints
            .Where(value => keySet.Contains(new ProjectionKey(value.ProjectionName, value.ProjectionVersion)))
            .Select(ToCheckpoint)
            .ToArray();
    }

    public async Task<IReadOnlyList<ProjectionFailure>> ReadFailuresAsync(
        string tenantId,
        ProjectionKey key,
        bool includeResolved,
        CancellationToken cancellationToken = default)
    {
        var failures = context.ProjectionFailures.AsNoTracking().Where(value =>
            value.TenantId == tenantId &&
            value.ProjectionName == key.Name &&
            value.ProjectionVersion == key.Version);
        if (!includeResolved)
        {
            failures = failures.Where(value => value.ResolvedAt == null);
        }

        var entities = await failures
            .OrderBy(value => value.TenantOffset)
            .ThenBy(value => value.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(ToFailure).ToArray();
    }

    public async Task<int> CountUnresolvedFailuresAsync(
        IReadOnlyCollection<ProjectionKey> keys,
        CancellationToken cancellationToken = default)
    {
        var keySet = keys.ToHashSet();
        var failures = await context.ProjectionFailures.AsNoTracking()
            .Where(value => value.ResolvedAt == null)
            .Select(value => new { value.ProjectionName, value.ProjectionVersion })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return failures.Count(value =>
            keySet.Contains(new ProjectionKey(value.ProjectionName, value.ProjectionVersion)));
    }

    public async Task<ProjectionDeliveryResult> ProcessAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        Func<IProjectionTransactionContext, CancellationToken, Task> apply,
        CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await VerifyLeaseAsync(tenantId, key, lease, cancellationToken).ConfigureAwait(false);
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
        if (checkpoint?.Status == ProjectionStatus.Paused)
        {
            return ProjectionDeliveryResult.Paused;
        }

        if (checkpoint is not null && envelope.TenantOffset <= checkpoint.TenantOffset)
        {
            return ProjectionDeliveryResult.AlreadyProcessed;
        }

        await apply(new EfProjectionTransaction(context), cancellationToken).ConfigureAwait(false);
        await VerifyLeaseAsync(tenantId, key, lease, cancellationToken).ConfigureAwait(false);
        checkpoint ??= NewCheckpoint(tenantId, key);
        if (context.Entry(checkpoint).State == EntityState.Detached)
        {
            context.ProjectionCheckpoints.Add(checkpoint);
        }

        checkpoint.TenantOffset = envelope.TenantOffset;
        checkpoint.Status = ProjectionStatus.Running;
        checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        await context.ProjectionFailures
            .Where(value =>
                value.TenantId == tenantId &&
                value.ProjectionName == key.Name &&
                value.ProjectionVersion == key.Version &&
                value.EventId == envelope.EventId &&
                value.ResolvedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(value => value.ResolvedAt, timeProvider.GetUtcNow())
                    .SetProperty(value => value.WasSkipped, false),
                cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ProjectionDeliveryResult.Processed;
    }

    public async Task RecordFailureAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        int attemptCount,
        string exceptionType,
        CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        await VerifyLeaseAsync(tenantId, key, lease, cancellationToken).ConfigureAwait(false);
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken).ConfigureAwait(false)
                         ?? NewCheckpoint(tenantId, key);
        if (context.Entry(checkpoint).State == EntityState.Detached)
        {
            context.ProjectionCheckpoints.Add(checkpoint);
        }

        var failure = await context.ProjectionFailures.SingleOrDefaultAsync(
            value =>
                value.TenantId == tenantId &&
                value.ProjectionName == key.Name &&
                value.ProjectionVersion == key.Version &&
                value.EventId == envelope.EventId &&
                value.ResolvedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            context.ProjectionFailures.Add(new ProjectionFailureEntity
            {
                TenantId = tenantId,
                ProjectionName = key.Name,
                ProjectionVersion = key.Version,
                EventId = envelope.EventId,
                EventType = envelope.EventType,
                TenantOffset = envelope.TenantOffset,
                AttemptCount = attemptCount,
                ExceptionType = exceptionType,
                FailedAt = timeProvider.GetUtcNow()
            });
        }
        else
        {
            failure.AttemptCount += attemptCount;
            failure.ExceptionType = exceptionType;
            failure.FailedAt = timeProvider.GetUtcNow();
        }

        checkpoint.Status = ProjectionStatus.Paused;
        checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ResumeAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null || checkpoint.Status != ProjectionStatus.Paused)
        {
            return false;
        }

        checkpoint.Status = ProjectionStatus.Running;
        checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SkipAsync(
        string tenantId,
        ProjectionKey key,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
        var failure = await context.ProjectionFailures.SingleOrDefaultAsync(
            value =>
                value.TenantId == tenantId &&
                value.ProjectionName == key.Name &&
                value.ProjectionVersion == key.Version &&
                value.EventId == eventId &&
                value.ResolvedAt == null,
            cancellationToken).ConfigureAwait(false);
        if (checkpoint?.Status != ProjectionStatus.Paused || failure is null)
        {
            return false;
        }

        checkpoint.TenantOffset = failure.TenantOffset;
        checkpoint.Status = ProjectionStatus.Running;
        checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        failure.ResolvedAt = timeProvider.GetUtcNow();
        failure.WasSkipped = true;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task ReplayAsync(string tenantId, ProjectionKey key, CancellationToken cancellationToken = default)
    {
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            context.ProjectionCheckpoints.Add(NewCheckpoint(tenantId, key));
        }
        else
        {
            checkpoint.TenantOffset = 0;
            checkpoint.Status = ProjectionStatus.Running;
            checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyLeaseAsync(
        string tenantId,
        ProjectionKey key,
        WorkerLease lease,
        CancellationToken cancellationToken)
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
            throw new ProjectionLeaseLostException(tenantId, key);
        }
    }

    private Task<ProjectionCheckpointEntity?> FindCheckpointAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken) =>
        context.ProjectionCheckpoints.SingleOrDefaultAsync(
            value =>
                value.TenantId == tenantId &&
                value.ProjectionName == key.Name &&
                value.ProjectionVersion == key.Version,
            cancellationToken);

    private ProjectionCheckpointEntity NewCheckpoint(string tenantId, ProjectionKey key) =>
        new()
        {
            TenantId = tenantId,
            ProjectionName = key.Name,
            ProjectionVersion = key.Version,
            TenantOffset = 0,
            Status = ProjectionStatus.Running,
            UpdatedAt = timeProvider.GetUtcNow()
        };

    private static ProjectionCheckpoint ToCheckpoint(ProjectionCheckpointEntity entity) =>
        new(
            entity.TenantId,
            new ProjectionKey(entity.ProjectionName, entity.ProjectionVersion),
            entity.TenantOffset,
            entity.Status,
            entity.UpdatedAt);

    private static ProjectionFailure ToFailure(ProjectionFailureEntity entity) =>
        new(
            entity.TenantId,
            new ProjectionKey(entity.ProjectionName, entity.ProjectionVersion),
            entity.EventId,
            entity.EventType,
            entity.TenantOffset,
            entity.AttemptCount,
            entity.ExceptionType,
            entity.FailedAt,
            entity.ResolvedAt,
            entity.WasSkipped);
}
