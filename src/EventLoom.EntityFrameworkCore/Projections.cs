using System.Data;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Handles a typed event in the EventLoom context transaction.</summary>
/// <typeparam name="TEvent">The domain event handled by the projection.</typeparam>
public interface IEfProjectionHandler<TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>Updates the read model for the typed event.</summary>
    /// <param name="envelope">The typed event and immutable persistence metadata.</param>
    /// <param name="context">The EventLoom context participating in the checkpoint transaction.</param>
    /// <param name="cancellationToken">Cancels projection execution.</param>
    Task HandleAsync(
        EventEnvelope<TEvent> envelope,
        EventStoreDbContext context,
        CancellationToken cancellationToken);
}

/// <summary>Identifies one version of a projection and its independent checkpoint.</summary>
public sealed record ProjectionKey(string Name, int Version)
{
    /// <summary>Validates the projection identity.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (Version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Version), "Projection versions must be positive.");
        }
    }
}

/// <summary>Describes whether a projection can continue processing events.</summary>
public enum ProjectionStatus
{
    /// <summary>The projection can process events.</summary>
    Running,

    /// <summary>The projection stopped after a persistent failure and requires administration.</summary>
    Paused
}

/// <summary>Describes a durable projection checkpoint for one tenant and projection version.</summary>
public sealed record ProjectionCheckpoint(
    string TenantId,
    ProjectionKey Key,
    long TenantOffset,
    ProjectionStatus Status,
    DateTimeOffset UpdatedAt);

/// <summary>Describes a safely persisted projection failure without event payload data.</summary>
public sealed record ProjectionFailure(
    string TenantId,
    ProjectionKey Key,
    Guid EventId,
    string EventType,
    long TenantOffset,
    int AttemptCount,
    string ExceptionType,
    DateTimeOffset FailedAt,
    DateTimeOffset? ResolvedAt,
    bool WasSkipped);

/// <summary>Describes the outcome of attempting one projection delivery.</summary>
public enum ProjectionDeliveryResult
{
    /// <summary>The event handler and checkpoint transaction committed.</summary>
    Processed,

    /// <summary>The event was already included in the durable checkpoint.</summary>
    AlreadyProcessed,

    /// <summary>The projection is paused and did not receive the event.</summary>
    Paused
}

/// <summary>Indicates that a projection worker lost its fenced lease before committing work.</summary>
public sealed class ProjectionLeaseLostException(string tenantId, ProjectionKey key)
    : InvalidOperationException(
        $"Projection '{key.Name}' version {key.Version} lost its lease for tenant '{tenantId}'.");

/// <summary>Persists checkpoints and failures and coordinates transactional EF projection work.</summary>
public sealed class ProjectionStore(EventStoreDbContext context, TimeProvider timeProvider)
{
    private readonly EventStoreDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Lists tenants with persisted events, in deterministic order.</summary>
    public async Task<IReadOnlyList<string>> ReadTenantIdsAsync(CancellationToken cancellationToken = default) =>
        await context.Events.AsNoTracking()
            .Select(value => value.TenantId)
            .Distinct()
            .OrderBy(value => value)
            .ToArrayAsync(cancellationToken);

    /// <summary>Gets the checkpoint for a tenant and projection version, if one exists.</summary>
    public async Task<ProjectionCheckpoint?> GetCheckpointAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        tenantId = Normalize(tenantId, key);
        var checkpoint = await context.ProjectionCheckpoints.AsNoTracking().SingleOrDefaultAsync(
            value =>
                value.TenantId == tenantId &&
                value.ProjectionName == key.Name &&
                value.ProjectionVersion == key.Version,
            cancellationToken);
        return checkpoint is null ? null : ToCheckpoint(checkpoint);
    }

    /// <summary>Lists persisted failures for a tenant and projection version.</summary>
    public async Task<IReadOnlyList<ProjectionFailure>> ReadFailuresAsync(
        string tenantId,
        ProjectionKey key,
        bool includeResolved = false,
        CancellationToken cancellationToken = default)
    {
        tenantId = Normalize(tenantId, key);
        var failures = context.ProjectionFailures.AsNoTracking().Where(
            value =>
                value.TenantId == tenantId &&
                value.ProjectionName == key.Name &&
                value.ProjectionVersion == key.Version);
        if (!includeResolved)
        {
            failures = failures.Where(value => value.ResolvedAt == null);
        }

        return await failures
            .OrderBy(value => value.TenantOffset)
            .ThenBy(value => value.Id)
            .Select(value => ToFailure(value))
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Runs one projection handler and advances its checkpoint in the same database transaction.
    /// </summary>
    public async Task<ProjectionDeliveryResult> ProcessAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        Func<EventStoreDbContext, CancellationToken, Task> apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(apply);
        tenantId = Normalize(tenantId, key);
        if (envelope.TenantId?.Value != tenantId)
        {
            throw new InvalidOperationException("The projection event does not belong to the requested tenant.");
        }

        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await VerifyLeaseAsync(tenantId, key, lease, cancellationToken);
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken);
        if (checkpoint?.Status == ProjectionStatus.Paused)
        {
            return ProjectionDeliveryResult.Paused;
        }

        if (checkpoint is not null && envelope.TenantOffset <= checkpoint.TenantOffset)
        {
            return ProjectionDeliveryResult.AlreadyProcessed;
        }

        await apply(context, cancellationToken);
        await VerifyLeaseAsync(tenantId, key, lease, cancellationToken);
        checkpoint ??= new ProjectionCheckpointEntity
        {
            TenantId = tenantId,
            ProjectionName = key.Name,
            ProjectionVersion = key.Version,
            TenantOffset = 0,
            Status = ProjectionStatus.Running,
            UpdatedAt = timeProvider.GetUtcNow()
        };
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
                cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProjectionDeliveryResult.Processed;
    }

    /// <summary>Records a terminal delivery failure and pauses its projection atomically.</summary>
    public async Task RecordFailureAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        int attemptCount,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(exception);
        tenantId = Normalize(tenantId, key);
        if (attemptCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptCount));
        }

        context.ChangeTracker.Clear();
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await VerifyLeaseAsync(tenantId, key, lease, cancellationToken);
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken)
            ?? new ProjectionCheckpointEntity
            {
                TenantId = tenantId,
                ProjectionName = key.Name,
                ProjectionVersion = key.Version,
                TenantOffset = 0,
                Status = ProjectionStatus.Running,
                UpdatedAt = timeProvider.GetUtcNow()
            };
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
            cancellationToken);
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
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                FailedAt = timeProvider.GetUtcNow()
            });
        }
        else
        {
            failure.AttemptCount += attemptCount;
            failure.ExceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            failure.FailedAt = timeProvider.GetUtcNow();
        }

        checkpoint.Status = ProjectionStatus.Paused;
        checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Resumes a paused projection so it can retry its failed event.</summary>
    public async Task<bool> ResumeAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        tenantId = Normalize(tenantId, key);
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken);
        if (checkpoint is null || checkpoint.Status != ProjectionStatus.Paused)
        {
            return false;
        }

        checkpoint.Status = ProjectionStatus.Running;
        checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Explicitly skips a paused projection's failed event and advances its checkpoint.</summary>
    public async Task<bool> SkipAsync(
        string tenantId,
        ProjectionKey key,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        tenantId = Normalize(tenantId, key);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken);
        var failure = await context.ProjectionFailures.SingleOrDefaultAsync(
            value =>
                value.TenantId == tenantId &&
                value.ProjectionName == key.Name &&
                value.ProjectionVersion == key.Version &&
                value.EventId == eventId &&
                value.ResolvedAt == null,
            cancellationToken);
        if (checkpoint?.Status != ProjectionStatus.Paused || failure is null)
        {
            return false;
        }

        checkpoint.TenantOffset = failure.TenantOffset;
        checkpoint.Status = ProjectionStatus.Running;
        checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        failure.ResolvedAt = timeProvider.GetUtcNow();
        failure.WasSkipped = true;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Resets a projection version's checkpoint to replay its tenant's complete event history.</summary>
    public async Task ReplayAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        tenantId = Normalize(tenantId, key);
        var checkpoint = await FindCheckpointAsync(tenantId, key, cancellationToken);
        if (checkpoint is null)
        {
            context.ProjectionCheckpoints.Add(new ProjectionCheckpointEntity
            {
                TenantId = tenantId,
                ProjectionName = key.Name,
                ProjectionVersion = key.Version,
                TenantOffset = 0,
                Status = ProjectionStatus.Running,
                UpdatedAt = timeProvider.GetUtcNow()
            });
        }
        else
        {
            checkpoint.TenantOffset = 0;
            checkpoint.Status = ProjectionStatus.Running;
            checkpoint.UpdatedAt = timeProvider.GetUtcNow();
        }

        await context.SaveChangesAsync(cancellationToken);
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
                value.LeaseName == GetLeaseName(key) &&
                value.OwnerId == lease.OwnerId &&
                value.FencingToken == lease.FencingToken,
            cancellationToken);
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

    /// <summary>Gets the stable lease name used by workers for a projection version.</summary>
    public static string GetLeaseName(ProjectionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        key.Validate();
        return $"projection:{key.Name}:v{key.Version}";
    }

    private static string Normalize(string tenantId, ProjectionKey key)
    {
        key.Validate();
        return new TenantId(tenantId).Value;
    }

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
