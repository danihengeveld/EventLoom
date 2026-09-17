using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// EF Core context containing EventLoom's event-store tables.
/// </summary>
/// <param name="options">The EF Core options configured for this context.</param>
/// <param name="eventStoreOptions">Optional event-store naming and schema settings.</param>
public sealed class EventStoreDbContext(
    DbContextOptions<EventStoreDbContext> options,
    EventStoreOptions? eventStoreOptions = null) : DbContext(options)
{
    private readonly EventStoreOptions configuration = eventStoreOptions ?? new();

    internal DbSet<StreamEntity> Streams => Set<StreamEntity>();
    internal DbSet<EventEntity> Events => Set<EventEntity>();
    internal DbSet<TenantOffsetEntity> TenantOffsets => Set<TenantOffsetEntity>();
    internal DbSet<SnapshotEntity> Snapshots => Set<SnapshotEntity>();
    internal DbSet<ProjectionCheckpointEntity> ProjectionCheckpoints => Set<ProjectionCheckpointEntity>();
    internal DbSet<ProjectionLeaseEntity> ProjectionLeases => Set<ProjectionLeaseEntity>();
    internal DbSet<ProjectionFailureEntity> ProjectionFailures => Set<ProjectionFailureEntity>();
    internal DbSet<OutboxEntity> Outbox => Set<OutboxEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyEventStoreConfiguration(configuration);
    }
}
