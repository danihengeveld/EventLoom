namespace EventLoom.Storage;

/// <summary>A transaction that a storage provider can share between EventLoom and application code.</summary>
/// <remarks>
/// Providers expose their native transaction through extension methods over a provider-specific
/// implementation of this interface. Disposing an uncommitted transaction rolls it back.
/// </remarks>
public interface IStorageTransaction : IAsyncDisposable
{
    /// <summary>Commits the transaction.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls the transaction back.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The provider-specific scope a transactional projection runs in. A provider passes its own implementation
/// to <c>Transactional</c> handlers so they can write read models in the checkpoint transaction.
/// </summary>
public interface IProjectionTransactionContext;

/// <summary>Persists and reads the immutable event history.</summary>
/// <remarks>
/// Implementations must enforce optimistic concurrency per stream, assign gapless tenant offsets that become
/// visible in offset order, scope every operation to a tenant, and make each append atomic.
/// </remarks>
public interface IEventStorage
{
    /// <summary>Begins a transaction that a subsequent <see cref="AppendAsync"/> can join.</summary>
    /// <exception cref="NotSupportedException">The provider does not support shared transactions.</exception>
    Task<IStorageTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);

    /// <summary>Appends a batch atomically, optionally inside a caller-owned transaction.</summary>
    /// <param name="request">The append to persist.</param>
    /// <param name="transaction">
    /// A transaction from <see cref="BeginTransactionAsync"/>, or <see langword="null"/> when the provider owns the
    /// transaction. The provider never commits or rolls back a caller-owned transaction.
    /// </param>
    /// <param name="cancellationToken">Cancels the append.</param>
    /// <returns>The committed events, or the original events for an idempotent replay.</returns>
    /// <exception cref="WrongExpectedVersionException">The stream version did not satisfy the expectation.</exception>
    /// <exception cref="EventStoreConcurrencyException">A concurrent writer won a race the provider cannot retry.</exception>
    Task<StorageAppendResult> AppendAsync(
        StorageAppendRequest request,
        IStorageTransaction? transaction,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a stream in stream-version order, bounded inclusively by the optional versions.</summary>
    Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(
        string tenantId,
        string streamId,
        long? fromVersion,
        long? toVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Reads committed events for a tenant in offset order, after the supplied offset.</summary>
    Task<IReadOnlyList<StoredEvent>> ReadTenantAsync(
        string tenantId,
        long afterOffset,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Lists every tenant with at least one event and its highest committed offset, ordered by tenant.</summary>
    Task<IReadOnlyList<TenantHead>> ReadTenantHeadsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Persists aggregate snapshots independently of event history.</summary>
public interface ISnapshotStorage
{
    /// <summary>Writes a snapshot and removes all but the newest <paramref name="snapshotsToRetain"/> for its stream.</summary>
    Task WriteAsync(SnapshotRecord snapshot, int snapshotsToRetain, CancellationToken cancellationToken = default);

    /// <summary>Reads the snapshot with the highest stream version for a stream and snapshot type.</summary>
    Task<SnapshotRecord?> ReadLatestAsync(
        string tenantId,
        string streamId,
        string aggregateType,
        string snapshotType,
        CancellationToken cancellationToken = default);

    /// <summary>Removes exactly one snapshot without touching event history.</summary>
    Task InvalidateAsync(SnapshotRecord snapshot, CancellationToken cancellationToken = default);
}

/// <summary>Describes the outcome of attempting one projection delivery.</summary>
public enum ProjectionDeliveryResult
{
    /// <summary>The handler ran and the checkpoint advanced.</summary>
    Processed,

    /// <summary>The event was already included in the durable checkpoint.</summary>
    AlreadyProcessed,

    /// <summary>The projection is paused and did not receive the event.</summary>
    Paused
}

/// <summary>Persists projection checkpoints and failures and runs deliveries under a fenced lease.</summary>
public interface IProjectionStorage
{
    /// <summary>Gets the checkpoint for a tenant and projection version, if one exists.</summary>
    Task<ProjectionCheckpoint?> GetCheckpointAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default);

    /// <summary>Reads every checkpoint belonging to the supplied projection versions, across tenants.</summary>
    Task<IReadOnlyList<ProjectionCheckpoint>> ReadCheckpointsAsync(
        IReadOnlyCollection<ProjectionKey> keys,
        CancellationToken cancellationToken = default);

    /// <summary>Lists persisted failures for a tenant and projection version, ordered by tenant offset.</summary>
    Task<IReadOnlyList<ProjectionFailure>> ReadFailuresAsync(
        string tenantId,
        ProjectionKey key,
        bool includeResolved,
        CancellationToken cancellationToken = default);

    /// <summary>Counts unresolved failures across tenants for the supplied projection versions.</summary>
    Task<int> CountUnresolvedFailuresAsync(
        IReadOnlyCollection<ProjectionKey> keys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers one event: verifies the fenced lease, skips paused or already processed deliveries, runs
    /// <paramref name="apply"/>, then advances the checkpoint and resolves open failures for the event.
    /// </summary>
    /// <remarks>
    /// Providers that support transactional projections run <paramref name="apply"/> and the checkpoint advance
    /// in one transaction. Others advance the checkpoint after <paramref name="apply"/> succeeds, which keeps
    /// delivery at least once.
    /// </remarks>
    /// <exception cref="ProjectionLeaseLostException">The lease no longer belongs to the caller.</exception>
    Task<ProjectionDeliveryResult> ProcessAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        Func<IProjectionTransactionContext, CancellationToken, Task> apply,
        CancellationToken cancellationToken = default);

    /// <summary>Records a terminal failure and pauses the projection atomically, under the fenced lease.</summary>
    /// <exception cref="ProjectionLeaseLostException">The lease no longer belongs to the caller.</exception>
    Task RecordFailureAsync(
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        int attemptCount,
        string exceptionType,
        CancellationToken cancellationToken = default);

    /// <summary>Resumes a paused projection. Returns <see langword="false"/> when it is not paused.</summary>
    Task<bool> ResumeAsync(string tenantId, ProjectionKey key, CancellationToken cancellationToken = default);

    /// <summary>Skips one unresolved failed event of a paused projection and resumes it.</summary>
    Task<bool> SkipAsync(
        string tenantId,
        ProjectionKey key,
        Guid eventId,
        CancellationToken cancellationToken = default);

    /// <summary>Resets a projection version's checkpoint so its tenant history is replayed from the start.</summary>
    Task ReplayAsync(string tenantId, ProjectionKey key, CancellationToken cancellationToken = default);
}

/// <summary>Reads durable outbox messages and records their delivery outcomes.</summary>
public interface IOutboxStorage
{
    /// <summary>Lists tenants with unpublished messages, ordered by tenant.</summary>
    Task<IReadOnlyList<string>> ReadPendingTenantIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts unpublished messages across tenants.</summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads unpublished messages for a tenant in tenant-offset order.</summary>
    Task<IReadOnlyList<OutboxMessage>> ReadPendingAsync(
        string tenantId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a message by tenant and message identifier.</summary>
    Task<OutboxMessage?> GetAsync(string tenantId, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Lists the recorded publication attempts of a message, ordered by attempt number.</summary>
    Task<IReadOnlyList<OutboxAttempt>> ReadAttemptsAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed attempt (<paramref name="exceptionType"/> is not <see langword="null"/>) or a successful
    /// publication, under the fenced lease. A successful publication deletes the message immediately when
    /// <paramref name="successfulDeliveryRetention"/> is zero and otherwise marks it published.
    /// </summary>
    /// <returns><see langword="false"/> when the message was already published.</returns>
    /// <exception cref="OutboxLeaseLostException">The lease no longer belongs to the caller.</exception>
    Task<bool> RecordAttemptAsync(
        OutboxMessage message,
        WorkerLease lease,
        string? exceptionType,
        TimeSpan successfulDeliveryRetention,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes published messages older than the retention, at most <paramref name="limit"/> at a time.</summary>
    Task<int> PurgePublishedAsync(
        TimeSpan successfulDeliveryRetention,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Grants tenant-scoped, time-limited leases with monotonically increasing fencing tokens.</summary>
public interface IWorkerLeaseStorage
{
    /// <summary>
    /// Acquires or renews a lease. Returns <see langword="null"/> when another owner holds an unexpired lease.
    /// Every successful call increments the fencing token.
    /// </summary>
    Task<WorkerLease?> TryAcquireAsync(
        string tenantId,
        string leaseName,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>Expires a lease only when its owner and fencing token still match; fencing tokens stay monotonic across releases.</summary>
    Task<bool> ReleaseAsync(WorkerLease lease, CancellationToken cancellationToken = default);
}

/// <summary>Creates and validates the structures a provider needs in its store.</summary>
public interface IStorageSchema
{
    /// <summary>
    /// Creates every structure the provider needs when it does not exist yet. This is an explicit
    /// development, test, and bootstrap operation and never migrates existing data.
    /// </summary>
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks connectivity and whether every expected structure exists, without exposing any data.</summary>
    Task<StorageValidationResult> ValidateAsync(CancellationToken cancellationToken = default);
}
