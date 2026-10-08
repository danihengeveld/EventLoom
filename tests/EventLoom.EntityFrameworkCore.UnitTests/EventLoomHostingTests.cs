using System.Data.Common;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SQLitePCL;

namespace EventLoom.UnitTests;

public sealed class EventLoomHostingTests
{
    [Test]
    public async Task AddEventLoomRegistersCoreServicesAndTypedRepository()
    {
        Batteries_V2.Init();
        var services = new ServiceCollection();

        services.AddEventLoom(eventLoom => eventLoom
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
        await Assert.That(scope.ServiceProvider.GetRequiredService<IEventIdGenerator>())
            .IsTypeOf<UuidV7EventIdGenerator>();
        await Assert.That(scope.ServiceProvider.GetRequiredService<TimeProvider>())
            .IsEqualTo(TimeProvider.System);
        await Assert.That(scope.ServiceProvider.GetRequiredService<AggregateRepository<Counter, Guid>>()).IsNotNull();
    }

    [Test]
    public async Task AddEventLoomUsesAnInternalSingleTenantByDefault()
    {
        var services = new ServiceCollection();
        services.AddEventLoom(eventLoom => eventLoom
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
    public async Task AddAggregateEvents_registers_owned_events_without_a_repository()
    {
        var services = new ServiceCollection();
        services.AddEventLoom(eventLoom => eventLoom
            .AddAggregateEvents<Counter>()
            .AddAggregateEvents<Counter>()
            .UseSqlite("Data Source=:memory:"));

        using var serviceProvider = services.BuildServiceProvider();
        var registry = serviceProvider.GetRequiredService<EventRegistry>();

        await Assert.That(registry.Registrations.Count).IsEqualTo(1);
        await Assert.That(registry.Get("tests.counter-incremented").AggregateType).IsEqualTo(typeof(Counter));
        await Assert.That(serviceProvider.GetService<AggregateRepository<Counter, Guid>>()).IsNull();
    }

    [Test]
    public async Task Duplicate_event_names_across_aggregates_fail_during_registration()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .UseSqlite("Data Source=:memory:")
                .AddAggregate<Counter, Guid>(aggregate => aggregate
                    .ConstructWith(id => new Counter(id))
                    .UseStream("counter", id => id.ToString("D")))
                .AddAggregate<DuplicateCounter, Guid>(aggregate => aggregate
                    .ConstructWith(id => new DuplicateCounter(id))
                    .UseStream("duplicate-counter", id => id.ToString("D")))))
            .Throws<DuplicateEventTypeException>();
    }

    [Test]
    public async Task MultiTenancyRequiresAnAccessor()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .AddAggregateEvents<Counter>()
                .UseSqlite("Data Source=:memory:")
                .UseMultiTenancy()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_storage_provider_is_required()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom.AddAggregateEvents<Counter>()))
            .Throws<InvalidOperationException>()
            .WithMessage(
                "Configure an EventLoom storage provider with a provider-specific extension such as UsePostgreSql, UseSqlite, or UseMongoDb.");
    }

    [Test]
    public async Task Only_one_storage_provider_can_be_configured()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .UseSqlite("Data Source=:memory:")
                .UseStorage(new StorageCapabilities("Other", true, true, true, true), _ => { })))
            .Throws<InvalidOperationException>()
            .WithMessage("An EventLoom storage provider has already been configured (SQLite).");
    }

    [Test]
    public async Task Projection_modes_the_provider_does_not_support_are_rejected_at_startup()
    {
        var limited = new StorageCapabilities("Limited", false, false, false, false);
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .AddAggregateEvents<Counter>()
                .UseStorage(limited, _ => { })
                .AddProjection("tests.transactional", projection => projection
                    .Transactional<NoopProjection, CounterIncremented>())))
            .Throws<InvalidOperationException>()
            .WithMessage("The Limited storage provider does not support transactional projections.");
        await Assert.That(() => new ServiceCollection().AddEventLoom(eventLoom => eventLoom
                .AddAggregateEvents<Counter>()
                .UseStorage(limited, _ => { })
                .AddProjection("tests.inline", projection => projection
                    .Inline<NoopProjection, CounterIncremented>())))
            .Throws<InvalidOperationException>()
            .WithMessage("The Limited storage provider does not support inline projections.");
    }

    [Test]
    public async Task Named_projection_registration_requires_at_least_one_handler()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .AddAggregateEvents<Counter>()
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
                .AddAggregateEvents<Counter>()
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
            .AddAggregateEvents<Counter>()
            .ConfigureEntityFramework(options => options.Schema = "custom")
            .UseSqlite("Data Source=:memory:"));

        using var sqliteProvider = sqliteServices.BuildServiceProvider();
        await Assert.That(sqliteProvider.GetRequiredService<EntityFrameworkStorageOptions>().UseSchema).IsFalse();

        var postgreSqlServices = new ServiceCollection();
        postgreSqlServices.AddEventLoom(eventLoom => eventLoom
            .AddAggregateEvents<Counter>()
            .ConfigureEntityFramework(options => options.Schema = "custom")
            .UsePostgreSql("Host=localhost;Database=eventloom;Username=eventloom;Password=eventloom"));

        using var postgreSqlProvider = postgreSqlServices.BuildServiceProvider();
        await Assert.That(postgreSqlProvider.GetRequiredService<EntityFrameworkStorageOptions>().UseSchema).IsTrue();
        await Assert.That(postgreSqlProvider.GetRequiredService<IEventStoreRetryPolicy>())
            .IsTypeOf<PostgreSqlRetryPolicy>();
    }

    [Test]
    public async Task Snapshot_repository_registration_uses_configured_retention()
    {
        var services = new ServiceCollection();

        services.AddEventLoom(eventLoom => eventLoom
            .UseSqlite("Data Source=:memory:")
            .ConfigureSnapshotRetention(new KeepLatestSnapshotsPolicy(2))
            .AddAggregate<Counter, Guid>(aggregate => aggregate
                .ConstructWith(id => new Counter(id))
                .UseStream("counter", id => id.ToString("D"))
                .UseSnapshots<CounterSnapshot>(snapshot => snapshot
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
            .UseSqlite("Data Source=:memory:")
            .AddAggregate<Counter, Guid>(aggregate => aggregate
                .ConstructWith(id => new Counter(id))
                .UseStream("counter", id => id.ToString("D"))
                .UseSnapshots<CounterSnapshot>(snapshot => snapshot
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
            .AddAggregateEvents<Counter>()
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
            .AddAggregateEvents<Counter>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}"));
        var provider = services.BuildServiceProvider();

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
            await provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private sealed class NoopProjection : IEfProjectionHandler<CounterIncremented>,
        IInlineProjectionHandler<CounterIncremented>
    {
        public Task HandleAsync(
            EventEnvelope<CounterIncremented> envelope,
            EventStoreDbContext context,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HandleAsync(EventEnvelope<CounterIncremented> envelope, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed record CounterIncremented : IDomainEvent<CounterIncremented, Counter>
    {
        public static string EventType => "tests.counter-incremented";
    }

    private sealed class CounterProjection : IProjectionHandler<CounterIncremented>
    {
        public Task HandleAsync(EventEnvelope<CounterIncremented> envelope, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class Counter(Guid id)
        : Aggregate<Counter, Guid>(id), IApply<CounterIncremented>, ISnapshotable<CounterSnapshot>
    {
        void IApply<CounterIncremented>.Apply(CounterIncremented @event)
        {
        }

        CounterSnapshot ISnapshotable<CounterSnapshot>.CreateSnapshot() => new();

        void ISnapshotable<CounterSnapshot>.RestoreSnapshot(CounterSnapshot snapshot)
        {
        }
    }

    private sealed record CounterSnapshot : IAggregateSnapshot<CounterSnapshot, Counter>
    {
        public static string SnapshotType => "tests.counter";
    }

    private sealed record DuplicateCounterIncremented : IDomainEvent<DuplicateCounterIncremented, DuplicateCounter>
    {
        public static string EventType => "tests.counter-incremented";
    }

    private sealed class DuplicateCounter(Guid id) : Aggregate<DuplicateCounter, Guid>(id),
        IApply<DuplicateCounterIncremented>
    {
        void IApply<DuplicateCounterIncremented>.Apply(DuplicateCounterIncremented @event)
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
