using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Provides tenant-scoped leases for distributed EventLoom workers.</summary>
public sealed class WorkerLeaseStore(EventStoreDbContext context, TimeProvider timeProvider)
{
    private readonly EventStoreDbContext context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Attempts to acquire or renew a lease for the specified owner.</summary>
    public async Task<WorkerLease?> TryAcquireAsync(
        string tenantId,
        string leaseName,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        tenantId = new TenantId(tenantId).Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var now = timeProvider.GetUtcNow();
        var lease = await context.ProjectionLeases.SingleOrDefaultAsync(
            value => value.TenantId == tenantId && value.LeaseName == leaseName,
            cancellationToken);
        if (lease is not null && lease.LeaseUntil > now && lease.OwnerId != ownerId)
        {
            return null;
        }

        if (lease is null)
        {
            var newLease = new ProjectionLeaseEntity
            {
                TenantId = tenantId,
                LeaseName = leaseName,
                OwnerId = ownerId,
                FencingToken = 1,
                LeaseUntil = now.Add(duration)
            };
            context.ProjectionLeases.Add(newLease);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return new WorkerLease(tenantId, leaseName, ownerId, newLease.FencingToken, newLease.LeaseUntil);
            }
            catch (DbUpdateException exception)
            {
                context.Entry(newLease).State = EntityState.Detached;
                throw new WorkerLeaseConflictException(tenantId, leaseName, exception);
            }
        }

        lease!.OwnerId = ownerId;
        lease.FencingToken++;
        lease.LeaseUntil = now.Add(duration);
        await context.SaveChangesAsync(cancellationToken);
        return new WorkerLease(tenantId, leaseName, ownerId, lease.FencingToken, lease.LeaseUntil);
    }

    /// <summary>Releases a lease only when its owner and fencing token still match.</summary>
    public async Task<bool> ReleaseAsync(WorkerLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var affected = await context.ProjectionLeases
            .Where(value =>
                value.TenantId == lease.TenantId &&
                value.LeaseName == lease.LeaseName &&
                value.OwnerId == lease.OwnerId &&
                value.FencingToken == lease.FencingToken)
            .ExecuteDeleteAsync(cancellationToken);
        return affected == 1;
    }
}

/// <summary>Describes an acquired tenant-scoped worker lease.</summary>
public sealed record WorkerLease(
    string TenantId,
    string LeaseName,
    string OwnerId,
    long FencingToken,
    DateTimeOffset LeaseUntil);

/// <summary>Indicates that a worker lease was lost to a concurrent owner.</summary>
public sealed class WorkerLeaseConflictException : InvalidOperationException
{
    /// <summary>Initializes a lease conflict exception.</summary>
    public WorkerLeaseConflictException(string tenantId, string leaseName, Exception innerException)
        : base($"Worker lease '{leaseName}' for tenant '{tenantId}' could not be acquired because it changed concurrently.", innerException)
    {
    }
}
