using EventLoom;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Data.Common;

namespace EventLoom.UnitTests;

public sealed class EventLoomHostingTests
{
    [Test]
    public async Task AddEventLoomRegistersCoreServicesAndTypedRepository()
    {
        SQLitePCL.Batteries_V2.Init();
        var services = new ServiceCollection();

        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<CounterIncremented>()
            .UseSqlite("Data Source=:memory:")
            .AddAggregate<Counter, Guid>(aggregate => aggregate
                .ConstructWith(id => new Counter(id))
                .UseStream("counter", id => id.ToString("D"))));

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
    public async Task AddEventLoomUsesAnInternalSingleTenantByDefault()
    {
        var services = new ServiceCollection();
        services.AddEventLoom(eventLoom => eventLoom
            .AddEvent<CounterIncremented>()
            .UseSqlite("Data Source=:memory:")
            .AddAggregate<Counter, Guid>(aggregate => aggregate
                .ConstructWith(id => new Counter(id))
                .UseStream("counter", id => id.ToString("D"))));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        await Assert.That(scope.ServiceProvider.GetRequiredService<ITenantAccessor>().TenantId)
            .IsEqualTo(new TenantId("default"));
        await Assert.That(scope.ServiceProvider.GetRequiredService<AggregateRepository<Counter, Guid>>()).IsNotNull();
    }

    [Test]
    public async Task MultiTenancyRequiresAnAccessor()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .AddEvent<CounterIncremented>()
                .UseSqlite("Data Source=:memory:")
                .ConfigureTenancy(TenancyMode.MultiTenant)))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Named_projection_registration_requires_at_least_one_handler()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .AddEvent<CounterIncremented>()
                .UseSqlite("Data Source=:memory:")
                .AddProjection("tests.empty", _ => { })))
            .Throws<InvalidOperationException>()
            .WithMessage("Projection 'tests.empty' version 1 must register at least one handler.");
    }

    [Test]
    public async Task Named_projection_registration_validates_duplicate_handlers_at_startup()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .AddEvent<CounterIncremented>()
                .UseSqlite("Data Source=:memory:")
                .AddProjection("tests.duplicate", projection => projection
                    .Asynchronous<CounterProjection, CounterIncremented>()
                    .Asynchronous<CounterProjection, CounterIncremented>())))
            .Throws<InvalidOperationException>()
            .WithMessage("A projection handler can only be registered once per event type and mode.");
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
            .AddAggregate<Counter, Guid>(aggregate => aggregate
                .ConstructWith(id => new Counter(id))
                .UseStream("counter", id => id.ToString("D"))
                .UseSnapshots(snapshot => snapshot
                    .UseAdapter(new CounterSnapshotAdapter())
                    .UseInvalidator(new AlwaysInvalidateSnapshots()))));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        await Assert.That(scope.ServiceProvider.GetRequiredService<ISnapshotRetentionPolicy>().SnapshotsToRetain)
            .IsEqualTo(2);
        await Assert.That(scope.ServiceProvider.GetRequiredService<SnapshotStore>()).IsNotNull();
        await Assert.That(scope.ServiceProvider.GetRequiredService<AggregateRepository<Counter, Guid>>()).IsNotNull();
    }

    [Test]
    public async Task AddAggregate_accepts_typed_snapshot_configuration()
    {
        var services = new ServiceCollection();

        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<CounterIncremented>()
            .UseSqlite("Data Source=:memory:")
            .AddAggregate<Counter, Guid>(aggregate => aggregate
                .ConstructWith(id => new Counter(id))
                .UseStream("counter", id => id.ToString("D"))
                .UseSnapshots(snapshot => snapshot
                    .UseAdapter(new CounterSnapshotAdapter())
                    .Every(2)
                    .KeepLatest(3)
                    .UseInvalidator(new AlwaysInvalidateSnapshots()))));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        await Assert.That(scope.ServiceProvider
                .GetRequiredService<AggregateRepository<Counter, Guid>>())
            .IsNotNull();
    }

    [Test]
    public async Task Shared_connection_provider_configuration_uses_the_scoped_connection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var services = new ServiceCollection();
        services.AddScoped<DbConnection>(_ => connection);
        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<CounterIncremented>()
            .UseSqlite(provider => provider.GetRequiredService<DbConnection>()));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var context = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();

        await Assert.That(ReferenceEquals(context.Database.GetDbConnection(), connection)).IsTrue();
    }

    [Test]
    public async Task Append_does_not_create_outbox_messages_without_a_publisher()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<CounterIncremented>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}"));
        await using var provider = services.BuildServiceProvider();

        try
        {
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
            await context.Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(new AppendRequest(
                "tenant-a",
                "counter-1",
                "counter",
                ExpectedVersion.NoStream,
                [new CounterIncremented()],
                new EventMetadata()));

            var pending = await scope.ServiceProvider.GetRequiredService<OutboxStore>()
                .ReadPendingAsync("tenant-a");
            await Assert.That(pending).IsEmpty();
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [EventType("tests.counter-incremented", Version = 1)]
    private sealed record CounterIncremented : IDomainEvent<Counter>;

    private sealed class CounterProjection : IProjectionHandler<CounterIncremented>
    {
        public Task HandleAsync(EventEnvelope<CounterIncremented> envelope, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

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
