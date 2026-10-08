using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Isolates the few places where EventLoom's EF storage needs provider-specific SQL or concurrency strategy.
/// </summary>
internal abstract class EfStorageDialect
{
    /// <summary>Gets the dialect used by providers without special requirements, such as SQLite.</summary>
    public static EfStorageDialect Default { get; } = new DefaultEfStorageDialect();

    /// <summary>
    /// Reads a tenant's offset row for an append, taking whatever lock the provider needs so concurrent
    /// appends to the same tenant serialize and offsets stay gapless. Returns <see langword="null"/> when the
    /// tenant has no offset row yet.
    /// </summary>
    public abstract Task<TenantOffsetEntity?> LockTenantOffsetAsync(
        EventStoreDbContext context,
        string tenantId,
        CancellationToken cancellationToken);

    /// <summary>Gets whether lease renewal and takeover run as one conditional database update.</summary>
    public abstract bool UsesConditionalLeaseUpdate { get; }

    /// <summary>Gets whether published outbox messages can be filtered by age in the database.</summary>
    public abstract bool FiltersPublishedOutboxInDatabase { get; }

    private sealed class DefaultEfStorageDialect : EfStorageDialect
    {
        public override Task<TenantOffsetEntity?> LockTenantOffsetAsync(
            EventStoreDbContext context,
            string tenantId,
            CancellationToken cancellationToken) =>
            context.TenantOffsets.SingleOrDefaultAsync(value => value.TenantId == tenantId, cancellationToken);

        public override bool UsesConditionalLeaseUpdate => false;

        public override bool FiltersPublishedOutboxInDatabase => false;
    }
}
