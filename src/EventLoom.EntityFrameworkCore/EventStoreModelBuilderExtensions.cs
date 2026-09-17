using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Provides EF Core model configuration for the EventLoom event store.
/// </summary>
public static class EventStoreModelBuilderExtensions
{
    /// <summary>
    /// Maps the event-store entities to tables using the supplied naming options.
    /// </summary>
    /// <param name="modelBuilder">The model builder being configured.</param>
    /// <param name="options">The schema, table prefix, and provider settings.</param>
    /// <returns>The same model builder instance for fluent configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelBuilder"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema or table prefix is empty.</exception>
    public static ModelBuilder ApplyEventStoreConfiguration(
        this ModelBuilder modelBuilder,
        EventStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Schema) || string.IsNullOrWhiteSpace(options.TablePrefix))
        {
            throw new ArgumentException("Event store schema and table prefix are required.", nameof(options));
        }

        var prefix = options.TablePrefix;
        var schema = options.UseSchema ? options.Schema : null;

        modelBuilder.Entity<StreamEntity>(entity =>
        {
            entity.ToTable($"{prefix}streams", schema);
            entity.HasKey(value => new { value.TenantId, value.StreamId });
            entity.Property(value => value.TenantId).HasMaxLength(256).IsRequired();
            entity.Property(value => value.StreamId).HasMaxLength(256);
            entity.Property(value => value.AggregateType).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.ToTable($"{prefix}events", schema);
            entity.HasKey(value => value.EventId);
            entity.Property(value => value.EventId).ValueGeneratedNever();
            entity.Property(value => value.TenantId).HasMaxLength(256).IsRequired();
            entity.Property(value => value.EventType).HasMaxLength(256).IsRequired();
            entity.Property(value => value.Payload).IsRequired();
            entity.HasIndex(value => new { value.TenantId, value.StreamId, value.StreamVersion }).IsUnique();
            entity.HasIndex(value => new { value.TenantId, value.TenantOffset }).IsUnique();
            entity.HasIndex(value => new { value.TenantId, value.AppendId });
        });

        modelBuilder.Entity<TenantOffsetEntity>(entity =>
        {
            entity.ToTable($"{prefix}offsets", schema);
            entity.HasKey(value => value.TenantId);
            entity.Property(value => value.TenantId).HasMaxLength(256).IsRequired();
        });
        ConfigureSimpleTable<SnapshotEntity>(modelBuilder, $"{prefix}snapshots", schema);
        ConfigureSimpleTable<ProjectionCheckpointEntity>(modelBuilder, $"{prefix}projection_checkpoints", schema);
        modelBuilder.Entity<ProjectionLeaseEntity>(entity =>
        {
            entity.ToTable($"{prefix}projection_leases", schema);
            entity.HasKey(value => new { value.TenantId, value.LeaseName });
            entity.Property(value => value.TenantId).HasMaxLength(256).IsRequired();
            entity.Property(value => value.LeaseName).HasMaxLength(256).IsRequired();
            entity.Property(value => value.OwnerId).HasMaxLength(256).IsRequired();
        });
        ConfigureSimpleTable<ProjectionFailureEntity>(modelBuilder, $"{prefix}projection_failures", schema);
        ConfigureSimpleTable<OutboxEntity>(modelBuilder, $"{prefix}outbox", schema);
        return modelBuilder;
    }

    private static void ConfigureSimpleTable<TEntity>(
        ModelBuilder modelBuilder,
        string tableName,
        string? schema)
        where TEntity : class, IEntityWithId
    {
        modelBuilder.Entity<TEntity>(entity =>
        {
            entity.ToTable(tableName, schema);
            entity.HasKey(value => value.Id);
        });
    }
}
