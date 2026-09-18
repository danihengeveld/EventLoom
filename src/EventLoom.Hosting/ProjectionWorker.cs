using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventLoom.Hosting;

/// <summary>Runs registered asynchronous projections with tenant-scoped leases and checkpoints.</summary>
internal sealed class ProjectionWorker(
    IServiceScopeFactory scopeFactory,
    ProjectionRegistry registry,
    EventStoreWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<ProjectionWorker> logger) : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ProjectionRegistry registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly EventStoreWorkerOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<ProjectionWorker> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            var progressed = await RunIterationAsync(stoppingToken);
            if (!progressed)
            {
                await Task.Delay(options.PollInterval, timeProvider, stoppingToken);
            }
        }
    }

    private async Task<bool> RunIterationAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var projectionStore = scope.ServiceProvider.GetRequiredService<ProjectionStore>();
        var tenants = await projectionStore.ReadTenantIdsAsync(cancellationToken);
        var progressed = false;
        foreach (var tenantId in tenants)
        {
            foreach (var key in registry.AsynchronousProjections)
            {
                progressed |= await RunBatchAsync(
                    scope.ServiceProvider,
                    tenantId,
                    key,
                    projectionStore,
                    cancellationToken);
            }
        }

        return progressed;
    }

    private async Task<bool> RunBatchAsync(
        IServiceProvider serviceProvider,
        string tenantId,
        ProjectionKey key,
        ProjectionStore projectionStore,
        CancellationToken cancellationToken)
    {
        var checkpoint = await projectionStore.GetCheckpointAsync(tenantId, key, cancellationToken);
        if (checkpoint?.Status == ProjectionStatus.Paused)
        {
            return false;
        }

        var leases = serviceProvider.GetRequiredService<WorkerLeaseStore>();
        var lease = await leases.TryAcquireAsync(
            tenantId,
            ProjectionStore.GetLeaseName(key),
            options.InstanceId,
            options.LeaseDuration,
            cancellationToken);
        if (lease is null)
        {
            return false;
        }

        try
        {
            var events = await serviceProvider.GetRequiredService<EventStore>().ReadTenantOffsetsForBackgroundAsync(
                tenantId,
                checkpoint?.TenantOffset ?? 0,
                options.BatchSize,
                cancellationToken);
            var processed = false;
            foreach (var envelope in events)
            {
                var outcome = await DeliverAsync(
                    serviceProvider,
                    tenantId,
                    key,
                    envelope,
                    lease,
                    projectionStore,
                    cancellationToken);
                if (outcome == ProjectionDeliveryResult.Paused)
                {
                    return processed;
                }

                processed |= outcome == ProjectionDeliveryResult.Processed;
                lease = await leases.TryAcquireAsync(
                    tenantId,
                    ProjectionStore.GetLeaseName(key),
                    options.InstanceId,
                    options.LeaseDuration,
                    cancellationToken) ?? throw new ProjectionLeaseLostException(tenantId, key);
            }

            return processed;
        }
        catch (ProjectionLeaseLostException)
        {
            EventLoomTelemetry.ProjectionLeaseLosses.Add(1);
            logger.LogDebug("Projection worker lost its lease before finishing a batch.");
            return false;
        }
        finally
        {
            await leases.ReleaseAsync(lease, cancellationToken);
        }
    }

    private async Task<ProjectionDeliveryResult> DeliverAsync(
        IServiceProvider serviceProvider,
        string tenantId,
        ProjectionKey key,
        EventEnvelope envelope,
        WorkerLease lease,
        ProjectionStore projectionStore,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (var attempt = 1; attempt <= options.MaxRetryAttempts + 1; attempt++)
        {
            try
            {
                var result = await projectionStore.ProcessAsync(
                    tenantId,
                    key,
                    envelope,
                    lease,
                    (context, token) => registry.DispatchAsynchronousAsync(
                        key,
                        serviceProvider,
                        envelope,
                        context,
                        token),
                    cancellationToken);
                EventLoomTelemetry.ProjectionDeliveries.Add(1);
                return result;
            }
            catch (ProjectionLeaseLostException)
            {
                throw;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                EventLoomTelemetry.ProjectionFailures.Add(1);
                logger.LogWarning(
                    "Projection {ProjectionName} v{ProjectionVersion} failed to process event {EventId} on attempt {Attempt} with {ExceptionType}.",
                    key.Name,
                    key.Version,
                    envelope.EventId,
                    attempt,
                    exception.GetType().FullName);
                failure = exception;
                if (attempt <= options.MaxRetryAttempts)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Min(1000, 50 * Math.Pow(2, attempt - 1)));
                    await Task.Delay(delay, timeProvider, cancellationToken);
                }
            }
        }

        await projectionStore.RecordFailureAsync(
            tenantId,
            key,
            envelope,
            lease,
            options.MaxRetryAttempts + 1,
            failure ?? throw new InvalidOperationException("Projection delivery did not report a failure."),
            cancellationToken);
        return ProjectionDeliveryResult.Paused;
    }
}

/// <summary>Provides explicit administration operations for a tenant-scoped projection.</summary>
public sealed class ProjectionAdministration(ProjectionStore store)
{
    private readonly ProjectionStore store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Gets a projection checkpoint, if it has begun processing events.</summary>
    public Task<ProjectionCheckpoint?> GetCheckpointAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default) =>
        store.GetCheckpointAsync(tenantId, key, cancellationToken);

    /// <summary>Lists persisted projection failures.</summary>
    public Task<IReadOnlyList<ProjectionFailure>> ReadFailuresAsync(
        string tenantId,
        ProjectionKey key,
        bool includeResolved = false,
        CancellationToken cancellationToken = default) =>
        store.ReadFailuresAsync(tenantId, key, includeResolved, cancellationToken);

    /// <summary>Resumes a paused projection so the failed event is retried.</summary>
    public Task<bool> ResumeAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default) =>
        store.ResumeAsync(tenantId, key, cancellationToken);

    /// <summary>Skips one persisted failed event and resumes the projection.</summary>
    public Task<bool> SkipAsync(
        string tenantId,
        ProjectionKey key,
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        store.SkipAsync(tenantId, key, eventId, cancellationToken);

    /// <summary>Resets the projection version's checkpoint for an explicit replay.</summary>
    public Task ReplayAsync(
        string tenantId,
        ProjectionKey key,
        CancellationToken cancellationToken = default) =>
        store.ReplayAsync(tenantId, key, cancellationToken);
}
