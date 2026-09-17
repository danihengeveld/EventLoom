using EventLoom;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.UnitTests;

public sealed class EventLoomHostingTests
{
    [Test]
    public async Task AddEventLoomRegistersCoreServicesAndTypedRepository()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        var services = new ServiceCollection();

        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<CounterIncremented>()
            .UseSqlite("Data Source=:memory:")
            .AddAggregateRepository<Counter, Guid>(
                id => new Counter(id),
                counter => counter.PendingEvents.Select(value => value.Event),
                counter => counter.Version));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        await Assert.That(scope.ServiceProvider.GetRequiredService<EventRegistry>().Get<CounterIncremented>().Name)
            .IsEqualTo("tests.counter-incremented");
        await Assert.That(scope.ServiceProvider.GetRequiredService<EventSerializer>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetRequiredService<EventStore>()).IsNotNull();
        var context = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
        await Assert.That(context.Database.ProviderName).IsEqualTo("Microsoft.EntityFrameworkCore.Sqlite");
        await Assert.That(scope.ServiceProvider.GetRequiredService<IEventIdGenerator>()).IsTypeOf<UuidV7EventIdGenerator>();
        await Assert.That(scope.ServiceProvider.GetRequiredService<TimeProviderClock>().Provider).IsEqualTo(TimeProvider.System);
        await Assert.That(scope.ServiceProvider.GetRequiredService<AggregateRepository<Counter, Guid>>()).IsNotNull();
    }

    [Test]
    public async Task ProviderHelpersApplyProviderSchemaDefaults()
    {
        var sqliteServices = new ServiceCollection();
        sqliteServices.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<CounterIncremented>()
            .ConfigureEventStore(options => options.UseSchema = true)
            .UseSqlite("Data Source=:memory:"));

        using var sqliteProvider = sqliteServices.BuildServiceProvider();
        await Assert.That(sqliteProvider.GetRequiredService<EventStoreOptions>().UseSchema).IsFalse();

        var postgreSqlServices = new ServiceCollection();
        postgreSqlServices.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<CounterIncremented>()
            .ConfigureEventStore(options => options.UseSchema = false)
            .UsePostgreSql("Host=localhost;Database=eventloom;Username=eventloom;Password=eventloom"));

        using var postgreSqlProvider = postgreSqlServices.BuildServiceProvider();
        await Assert.That(postgreSqlProvider.GetRequiredService<EventStoreOptions>().UseSchema).IsTrue();
        await Assert.That(postgreSqlProvider.GetRequiredService<IEventStoreRetryPolicy>())
            .IsTypeOf<PostgreSqlRetryPolicy>();
    }

    [Test]
    public async Task Snapshot_repository_registration_uses_configured_retention()
    {
        var services = new ServiceCollection();

        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<CounterIncremented>()
            .UseSqlite("Data Source=:memory:")
            .ConfigureSnapshotRetention(new KeepLatestSnapshotsPolicy(2))
            .AddAggregateRepository<Counter, Guid>(
                id => new Counter(id),
                aggregateType: "counter",
                streamId: id => id.ToString("D"),
                snapshotAdapter: new CounterSnapshotAdapter(),
                snapshotInvalidator: new AlwaysInvalidateSnapshots()));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        await Assert.That(scope.ServiceProvider.GetRequiredService<ISnapshotRetentionPolicy>().SnapshotsToRetain)
            .IsEqualTo(2);
        await Assert.That(scope.ServiceProvider.GetRequiredService<SnapshotStore>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetRequiredService<AggregateRepository<Counter, Guid>>()).IsNotNull();
    }

    [EventType("tests.counter-incremented", Version = 1)]
    private sealed record CounterIncremented : IDomainEvent;

    private sealed class Counter(Guid id) : Aggregate<Guid>(id);

    [SnapshotType("tests.counter", Version = 1)]
    private sealed record CounterSnapshot : IAggregateSnapshot;

    private sealed class CounterSnapshotAdapter : IAggregateSnapshotAdapter<Counter>
    {
        public string SnapshotType => "tests.counter";
        public int SchemaVersion => 1;
        public string Capture(Counter aggregate) => "{}";
        public void Restore(Counter aggregate, int schemaVersion, string payload)
        {
        }
    }

    private sealed class AlwaysInvalidateSnapshots : ISnapshotInvalidator
    {
        public bool ShouldInvalidate(
            string snapshotType,
            int schemaVersion,
            SnapshotInvalidationReason reason) => true;
    }
}
