using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventLoom.Storage;

/// <summary>Identifies one version of a projection and its independent checkpoint.</summary>
public sealed record ProjectionKey(string Name, int Version)
{
    /// <summary>Validates the projection identity.</summary>
    internal void Validate()
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

/// <summary>Summarizes asynchronous projection state for operational health checks.</summary>
public sealed record ProjectionHealthSummary(
    int ProjectionCount,
    int UnresolvedFailureCount,
    long MaximumLag);

/// <summary>Indicates that a projection worker lost its fenced lease before committing work.</summary>
public sealed class ProjectionLeaseLostException(string tenantId, ProjectionKey key)
    : InvalidOperationException(
        $"Projection '{key.Name}' version {key.Version} lost its lease for tenant '{tenantId}'.");

/// <summary>Validates projection requests and derives operational summaries over a provider's projection storage.</summary>
internal sealed class ProjectionStore(
    IProjectionStorage storage,
    IEventStorage events,
    ILogger<ProjectionStore>? logger = null)
{
    private readonly IProjectionStorage storage = storage ?? throw new ArgumentNullException(nameof(storage));
    private readonly IEventStorage events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ILogger<ProjectionStore> logger = logger ?? NullLogger<ProjectionStore>.Instance;

    /// <summary>Lists tenants with persisted events, in deterministic order.</summary>
    public async Task<IReadOnlyList<string>> ReadTenantIdsAsync(CancellationToken cancellationToken = default) =>
        (await events.ReadTenantHeadsAsync(cancellationToken).ConfigureAwait(false))
        .Select(value => value.TenantId)
        .ToArray();

    /// <summary>Gets the checkpoint for a tenant and projection version, if one exists.</summary>
    public Task<ProjectionCheckpoint?> GetCheckpointAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default) =>
        storage.GetCheckpointAsync(Normalize(tenantId, key), key, cancellationToken);

    /// <summary>Lists persisted failures for a tenant and projection version.</summary>
    public Task<IReadOnlyList<ProjectionFailure>> ReadFailuresAsync(
        string tenantId,
        ProjectionKey key,
        bool includeResolved = false,
        CancellationToken cancellationToken = default) =>
        storage.ReadFailuresAsync(Normalize(tenantId, key), key, includeResolved, cancellationToken);

    /// <summary>
    /// Summarizes unresolved failures and event-offset lag for the supplied asynchronous projections.
    /// </summary>
    /// <remarks>
    /// The result contains counts and offsets only; it never includes event payloads or tenant identities.
    /// </remarks>
    public async Task<ProjectionHealthSummary> GetHealthSummaryAsync(
        IEnumerable<ProjectionKey> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var projections = keys.Distinct().ToArray();
        foreach (var key in projections)
        {
            key.Validate();
        }

        if (projections.Length == 0)
        {
            return new ProjectionHealthSummary(0, 0, 0);
        }

        var heads = await events.ReadTenantHeadsAsync(cancellationToken).ConfigureAwait(false);
        var checkpoints = await storage.ReadCheckpointsAsync(projections, cancellationToken).ConfigureAwait(false);
        var unresolved = await storage.CountUnresolvedFailuresAsync(projections, cancellationToken)
            .ConfigureAwait(false);
        var checkpointOffsets = checkpoints.ToDictionary(
            value => (value.TenantId, value.Key),
            value => value.TenantOffset);
        var maximumLag = heads
            .SelectMany(head => projections, (head, key) =>
                Math.Max(0, head.LastOffset - checkpointOffsets.GetValueOrDefault((head.TenantId, key))))
            .DefaultIfEmpty(0)
            .Max();
        return new ProjectionHealthSummary(projections.Length, unresolved, maximumLag);
    }

    /// <summary>Runs one projection handler and advances its checkpoint under the fenced lease.</summary>
    public Task<ProjectionDeliveryResult> ProcessAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        Func<IProjectionTransactionContext, CancellationToken, Task> apply,
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

        return storage.ProcessAsync(tenantId, key, envelope, lease, apply, cancellationToken);
    }

    /// <summary>Records a terminal delivery failure and pauses its projection atomically.</summary>
    public Task RecordFailureAsync(
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

        return storage.RecordFailureAsync(
            tenantId,
            key,
            envelope,
            lease,
            attemptCount,
            exception.GetType().FullName ?? exception.GetType().Name,
            cancellationToken);
    }

    /// <summary>Resumes a paused projection so it can retry its failed event.</summary>
    public async Task<bool> ResumeAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        var resumed = await storage.ResumeAsync(Normalize(tenantId, key), key, cancellationToken)
            .ConfigureAwait(false);
        if (resumed)
        {
            logger.ProjectionResumed(key.Name, key.Version);
        }

        return resumed;
    }

    /// <summary>Explicitly skips a paused projection's failed event and advances its checkpoint.</summary>
    public async Task<bool> SkipAsync(
        string tenantId,
        ProjectionKey key,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var skipped = await storage.SkipAsync(Normalize(tenantId, key), key, eventId, cancellationToken)
            .ConfigureAwait(false);
        if (skipped)
        {
            logger.ProjectionSkipped(key.Name, key.Version);
        }

        return skipped;
    }

    /// <summary>Resets a projection version's checkpoint to replay its tenant's complete event history.</summary>
    public async Task ReplayAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default)
    {
        await storage.ReplayAsync(Normalize(tenantId, key), key, cancellationToken).ConfigureAwait(false);
        logger.ProjectionReplayStarted(key.Name, key.Version);
    }

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
}
