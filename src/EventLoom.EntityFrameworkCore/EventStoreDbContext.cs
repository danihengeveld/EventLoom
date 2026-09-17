using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// EF Core context containing EventLoom's event-store tables.
/// </summary>
/// <param name="options">The EF Core options configured for this context.</param>
/// <param name="eventStoreOptions">Optional event-store naming and schema settings.</param>
/// <param name="configureModel">Optional application read-model mappings for transactional projections.</param>
public sealed class EventStoreDbContext(
    DbContextOptions<EventStoreDbContext> options,
    EventStoreOptions? eventStoreOptions = null,
    Action<ModelBuilder>? configureModel = null) : DbContext(options)
{
    private readonly EventStoreOptions configuration = eventStoreOptions ?? new();
    private readonly Action<ModelBuilder>? configureModel = configureModel;

    internal EventStoreOptions Configuration => configuration;
    internal Action<ModelBuilder>? ModelConfiguration => configureModel;

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
        configureModel?.Invoke(modelBuilder);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, EventStoreModelCacheKeyFactory>();
    }
}
