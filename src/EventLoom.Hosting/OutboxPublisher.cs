using EventLoom.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventLoom.Hosting;

/// <summary>Publishes one durable EventLoom outbox message to an external transport.</summary>
public interface IOutboxPublisher
{
    /// <summary>Publishes a message using its stable <see cref="OutboxMessage.MessageId"/> as the transport idempotency key.</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>Runs registered outbox publishers under tenant-scoped fenced leases.</summary>
internal sealed class OutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    EventStoreWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly EventStoreWorkerOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<OutboxPublisherWorker> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await RunIterationAsync(stoppingToken))
            {
                await Task.Delay(options.PollInterval, timeProvider, stoppingToken);
            }
        }
    }

    private async Task<bool> RunIterationAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<OutboxStore>();
        var progressed = false;
        foreach (var tenantId in await store.ReadPendingTenantIdsAsync(cancellationToken))
        {
            progressed |= await RunTenantAsync(scope.ServiceProvider, store, tenantId, cancellationToken);
        }

        return progressed;
    }

    private async Task<bool> RunTenantAsync(
        IServiceProvider services,
        OutboxStore store,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var leases = services.GetRequiredService<WorkerLeaseStore>();
        var lease = await leases.TryAcquireAsync(
            tenantId,
            OutboxStore.GetLeaseName(),
            options.InstanceId,
            options.LeaseDuration,
            cancellationToken);
        if (lease is null)
        {
            return false;
        }

        try
        {
            var progressed = false;
            foreach (var message in await store.ReadPendingAsync(tenantId, options.BatchSize, cancellationToken))
            {
                progressed |= await PublishAsync(services, store, message, lease, cancellationToken);
                lease = await leases.TryAcquireAsync(
                    tenantId,
                    OutboxStore.GetLeaseName(),
                    options.InstanceId,
                    options.LeaseDuration,
                    cancellationToken) ?? throw new OutboxLeaseLostException(tenantId);
            }

            return progressed;
        }
        catch (OutboxLeaseLostException)
        {
            logger.LogDebug("Outbox publisher lost its lease before finishing a batch.");
            return false;
        }
        finally
        {
            await leases.ReleaseAsync(lease, cancellationToken);
        }
    }

    private async Task<bool> PublishAsync(
        IServiceProvider services,
        OutboxStore store,
        OutboxMessage message,
        WorkerLease lease,
        CancellationToken cancellationToken)
    {
        var publisher = services.GetRequiredService<IOutboxPublisher>();
        for (var attempt = 0; attempt <= options.MaxRetryAttempts; attempt++)
        {
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
                var recorded = await store.RecordAttemptAsync(message, lease, exception: null, cancellationToken);
                EventLoomTelemetry.OutboxDeliveries.Add(1);
                return recorded;
            }
            catch (OutboxLeaseLostException)
            {
                throw;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                EventLoomTelemetry.OutboxFailures.Add(1);
                logger.LogWarning(
                    "Outbox publication failed for message {MessageId} on attempt {Attempt} with {ExceptionType}.",
                    message.MessageId,
                    attempt + 1,
                    exception.GetType().FullName);
                await store.RecordAttemptAsync(message, lease, exception, cancellationToken);
                if (attempt < options.MaxRetryAttempts)
                {
                    var delay = TimeSpan.FromMilliseconds(Math.Min(1000, 50 * Math.Pow(2, attempt)));
                    await Task.Delay(delay, timeProvider, cancellationToken);
                }
            }
        }

        return false;
    }
}

/// <summary>Provides inspection operations for durable EventLoom outbox messages.</summary>
public sealed class OutboxAdministration(OutboxStore store)
{
    private readonly OutboxStore store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Gets a message by tenant-scoped stable message identifier.</summary>
    public Task<OutboxMessage?> GetAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        store.GetAsync(tenantId, messageId, cancellationToken);

    /// <summary>Lists the completed publication attempts for a message.</summary>
    public Task<IReadOnlyList<OutboxAttempt>> ReadAttemptsAsync(
        string tenantId,
        Guid messageId,
        CancellationToken cancellationToken = default) =>
        store.ReadAttemptsAsync(tenantId, messageId, cancellationToken);
}
