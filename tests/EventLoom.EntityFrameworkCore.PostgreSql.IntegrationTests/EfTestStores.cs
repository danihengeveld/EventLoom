using EventLoom.EntityFrameworkCore.PostgreSql;
using Microsoft.Extensions.Logging;

namespace EventLoom.EntityFrameworkCore.TestSupport;

/// <summary>Builds EventLoom's storage engine over the EF Core provider for tests that construct stores directly.</summary>
internal static class EfTestStores
{
    public static EfStorageDialect Dialect { get; } = new PostgreSqlStorageDialect();

    public static EfEventStorage Storage(EventStoreDbContext context) => new(context, Dialect);

    public static EventStore EventStore(
        EventStoreDbContext context,
        EventSerializer serializer,
        IEventIdGenerator eventIdGenerator,
        TimeProvider timeProvider,
        EventStoreOptions? options = null,
        ITenantAccessor? tenantAccessor = null,
        IEventStoreRetryPolicy? retryPolicy = null,
        IInlineProjectionDispatcher? inlineDispatcher = null,
        ILogger<EventStore>? logger = null) =>
        new(Storage(context), serializer, eventIdGenerator, timeProvider, options, tenantAccessor, retryPolicy,
            inlineDispatcher, logger);

    public static ProjectionStore Projections(
        EventStoreDbContext context,
        TimeProvider timeProvider,
        ILogger<ProjectionStore>? logger = null) =>
        new(new EfProjectionStorage(context, timeProvider), Storage(context), logger);

    public static OutboxStore Outbox(EventStoreDbContext context, TimeProvider timeProvider) =>
        new(new EfOutboxStorage(context, Dialect, timeProvider));

    public static SnapshotStore Snapshots(
        EventStoreDbContext context,
        TimeProvider timeProvider,
        ISnapshotRetentionPolicy? retentionPolicy = null) =>
        new(new EfSnapshotStorage(context), timeProvider, retentionPolicy);

    public static EfWorkerLeaseStorage Leases(EventStoreDbContext context, TimeProvider timeProvider) =>
        new(context, Dialect, timeProvider);
}
