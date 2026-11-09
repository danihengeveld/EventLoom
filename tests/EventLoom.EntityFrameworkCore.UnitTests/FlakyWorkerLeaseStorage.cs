using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.EntityFrameworkCore.UnitTests;

/// <summary>Counts the lease acquisitions that are made to fail.</summary>
internal sealed class LeaseFailureBudget(int failures)
{
    private readonly int total = failures;
    private int remaining = failures;

    public int Failed => total - Math.Max(Volatile.Read(ref remaining), 0);

    public bool TryConsume() => Interlocked.Decrement(ref remaining) >= 0;
}

/// <summary>Wraps the registered lease storage and throws for the first acquisitions, simulating a storage outage.</summary>
internal sealed class FlakyWorkerLeaseStorage(IWorkerLeaseStorage inner, LeaseFailureBudget budget)
    : IWorkerLeaseStorage
{
    public Task<WorkerLease?> TryAcquireAsync(
        string tenantId,
        string leaseName,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default) =>
        budget.TryConsume()
            ? throw new TimeoutException("The lease store is unavailable.")
            : inner.TryAcquireAsync(tenantId, leaseName, ownerId, duration, cancellationToken);

    public Task<bool> ReleaseAsync(WorkerLease lease, CancellationToken cancellationToken = default) =>
        inner.ReleaseAsync(lease, cancellationToken);

    /// <summary>Replaces the provider's lease storage registration with a flaky decorator.</summary>
    public static void Decorate(IServiceCollection services, LeaseFailureBudget budget)
    {
        var descriptor = services.Last(service => service.ServiceType == typeof(IWorkerLeaseStorage));
        var implementationType = descriptor.ImplementationType
            ?? throw new InvalidOperationException("Expected a type-based lease storage registration.");
        services.Add(new ServiceDescriptor(implementationType, implementationType, descriptor.Lifetime));
        services.Add(new ServiceDescriptor(
            typeof(IWorkerLeaseStorage),
            serviceProvider => new FlakyWorkerLeaseStorage(
                (IWorkerLeaseStorage)serviceProvider.GetRequiredService(implementationType),
                budget),
            descriptor.Lifetime));
        services.AddSingleton(budget);
    }
}
