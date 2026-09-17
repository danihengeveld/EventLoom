using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

public sealed class EventStoreDbContext(
    DbContextOptions<EventStoreDbContext> options,
    EventStoreOptions? eventStoreOptions = null) : DbContext(options)
{
    private readonly EventStoreOptions configuration = eventStoreOptions ?? new();

    internal DbSet<StreamEntity> Streams => Set<StreamEntity>();
    internal DbSet<EventEntity> Events => Set<EventEntity>();
    internal DbSet<TenantPositionEntity> TenantPositions => Set<TenantPositionEntity>();
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
