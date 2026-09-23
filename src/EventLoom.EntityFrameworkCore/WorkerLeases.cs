using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Provides tenant-scoped leases for distributed EventLoom workers.</summary>
internal sealed class WorkerLeaseStore(EventStoreDbContext context, TimeProvider timeProvider)
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
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        var now = timeProvider.GetUtcNow();
        var isPostgreSql = context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ==
                           true;
        if (isPostgreSql)
        {
            var leaseUntil = now.Add(duration);
            var updated = await context.ProjectionLeases
                .Where(value =>
                    value.TenantId == tenantId &&
                    value.LeaseName == leaseName &&
                    (value.LeaseUntil <= now || value.OwnerId == ownerId))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(value => value.OwnerId, ownerId)
                        .SetProperty(value => value.FencingToken, value => value.FencingToken + 1)
                        .SetProperty(value => value.LeaseUntil, leaseUntil),
                    cancellationToken).ConfigureAwait(false);
            if (updated == 1)
            {
                var renewed = await context.ProjectionLeases.AsNoTracking().SingleAsync(
                    value => value.TenantId == tenantId && value.LeaseName == leaseName,
                    cancellationToken).ConfigureAwait(false);
                return renewed.OwnerId == ownerId && renewed.LeaseUntil > timeProvider.GetUtcNow()
                    ? new WorkerLease(tenantId, leaseName, ownerId, renewed.FencingToken, renewed.LeaseUntil)
                    : null;
            }
        }

        var leases = isPostgreSql ? context.ProjectionLeases.AsNoTracking() : context.ProjectionLeases;
        var lease = await leases.SingleOrDefaultAsync(
            value => value.TenantId == tenantId && value.LeaseName == leaseName,
            cancellationToken).ConfigureAwait(false);

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
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new WorkerLease(tenantId, leaseName, ownerId, newLease.FencingToken, newLease.LeaseUntil);
            }
            catch (DbUpdateException exception)
            {
                context.ChangeTracker.Clear();
                var current = await context.ProjectionLeases.AsNoTracking().SingleOrDefaultAsync(
                    value => value.TenantId == tenantId && value.LeaseName == leaseName,
                    cancellationToken).ConfigureAwait(false);
                if (current is not null && current.LeaseUntil > now && current.OwnerId != ownerId)
                {
                    return null;
                }

                throw new WorkerLeaseConflictException(tenantId, leaseName, exception);
            }
        }

        if (!isPostgreSql)
        {
            if (lease.LeaseUntil > now && lease.OwnerId != ownerId)
            {
                return null;
            }

            lease.OwnerId = ownerId;
            lease.FencingToken++;
            lease.LeaseUntil = now.Add(duration);
            context.ProjectionLeases.Update(lease);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new WorkerLease(tenantId, leaseName, ownerId, lease.FencingToken, lease.LeaseUntil);
        }

        return null;
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
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }
}

/// <summary>Describes an acquired tenant-scoped worker lease.</summary>
internal sealed record WorkerLease(
    string TenantId,
    string LeaseName,
    string OwnerId,
    long FencingToken,
    DateTimeOffset LeaseUntil);

/// <summary>Indicates that a worker lease was lost to a concurrent owner.</summary>
/// <remarks>Initializes a lease conflict exception.</remarks>
internal sealed class WorkerLeaseConflictException(string tenantId, string leaseName, Exception innerException)
    : InvalidOperationException(
        $"Worker lease '{leaseName}' for tenant '{tenantId}' could not be acquired because it changed concurrently.",
        innerException);
