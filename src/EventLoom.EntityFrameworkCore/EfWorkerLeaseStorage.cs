using EventLoom.Storage;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Grants tenant-scoped fenced leases through an EF Core context.</summary>
internal sealed class EfWorkerLeaseStorage(
    EventStoreDbContext context,
    EfStorageDialect dialect,
    TimeProvider timeProvider) : IWorkerLeaseStorage
{
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
        var conditionalUpdate = dialect.UsesConditionalLeaseUpdate;
        if (conditionalUpdate)
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

        var leases = conditionalUpdate ? context.ProjectionLeases.AsNoTracking() : context.ProjectionLeases;
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

        if (!conditionalUpdate)
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

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(WorkerLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var now = timeProvider.GetUtcNow();
        if (dialect.UsesConditionalLeaseUpdate)
        {
            var affected = await context.ProjectionLeases
                .Where(value =>
                    value.TenantId == lease.TenantId &&
                    value.LeaseName == lease.LeaseName &&
                    value.OwnerId == lease.OwnerId &&
                    value.FencingToken == lease.FencingToken &&
                    value.LeaseUntil > now)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(value => value.LeaseUntil, now),
                    cancellationToken).ConfigureAwait(false);
            return affected == 1;
        }

        var current = await context.ProjectionLeases.SingleOrDefaultAsync(
            value => value.TenantId == lease.TenantId && value.LeaseName == lease.LeaseName,
            cancellationToken).ConfigureAwait(false);
        if (current is null ||
            current.OwnerId != lease.OwnerId ||
            current.FencingToken != lease.FencingToken ||
            current.LeaseUntil <= now)
        {
            return false;
        }

        current.LeaseUntil = now;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>Indicates that a worker lease was lost to a concurrent owner.</summary>
internal sealed class WorkerLeaseConflictException(string tenantId, string leaseName, Exception innerException)
    : InvalidOperationException(
        $"Worker lease '{leaseName}' for tenant '{tenantId}' could not be acquired because it changed concurrently.",
        innerException);
