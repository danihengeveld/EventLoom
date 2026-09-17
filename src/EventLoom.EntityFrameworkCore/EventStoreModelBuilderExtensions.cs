using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

public static class EventStoreModelBuilderExtensions
{
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
            entity.Property(value => value.StreamId).HasMaxLength(256);
            entity.Property(value => value.AggregateType).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<EventEntity>(entity =>
        {
            entity.ToTable($"{prefix}events", schema);
            entity.HasKey(value => value.EventId);
            entity.Property(value => value.EventId).ValueGeneratedNever();
            entity.Property(value => value.EventType).HasMaxLength(256).IsRequired();
            entity.Property(value => value.Payload).IsRequired();
            entity.HasIndex(value => new { value.TenantId, value.StreamId, value.StreamVersion }).IsUnique();
            entity.HasIndex(value => new { value.TenantId, value.GlobalPosition }).IsUnique();
        });

        ConfigureSimpleTable< TenantPositionEntity>(modelBuilder, $"{prefix}positions", schema);
        ConfigureSimpleTable<SnapshotEntity>(modelBuilder, $"{prefix}snapshots", schema);
        ConfigureSimpleTable<ProjectionCheckpointEntity>(modelBuilder, $"{prefix}projection_checkpoints", schema);
        ConfigureSimpleTable<ProjectionLeaseEntity>(modelBuilder, $"{prefix}projection_leases", schema);
        ConfigureSimpleTable<ProjectionFailureEntity>(modelBuilder, $"{prefix}projection_failures", schema);
        ConfigureSimpleTable<OutboxEntity>(modelBuilder, $"{prefix}outbox", schema);
        return modelBuilder;
    }

    private static void ConfigureSimpleTable<TEntity>(
        ModelBuilder modelBuilder,
        string tableName,
        string? schema)
        where TEntity : class
    {
        modelBuilder.Entity<TEntity>().ToTable(tableName, schema);
    }
}
