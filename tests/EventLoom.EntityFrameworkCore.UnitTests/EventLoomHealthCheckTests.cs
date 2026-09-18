using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SQLitePCL;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class EventLoomHealthCheckTests
{
    [Test]
    public async Task Health_checks_validate_schema_and_report_projection_and_outbox_work()
    {
        Batteries_V2.Init();
        var databasePath = Path.Combine(
            Environment.CurrentDirectory,
            $"eventloom-health-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEventLoom(eventLoom => eventLoom
            .AddEvent<ItemAdded>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}")
            .AddProjection<RecordingProjection, ItemAdded>("tests.health")
            .AddOutboxPublisher<NoopOutboxPublisher>());
        services.AddEventLoomHealthChecks(options =>
        {
            options.MaximumProjectionLag = 0;
            options.MaximumOutboxBacklog = 0;
        });
        var provider = services.BuildServiceProvider();

        try
        {
            var healthChecks = provider.GetRequiredService<HealthCheckService>();
            var missingSchema = await healthChecks.CheckHealthAsync();
            await Assert.That(missingSchema.Entries["eventloom.event-store"].Status)
                .IsEqualTo(HealthStatus.Unhealthy);

            await EnsureCreatedAsync(provider);
            await using (var scope = provider.CreateAsyncScope())
            {
                var validation = await EventStoreSchema.ValidateAsync(
                    scope.ServiceProvider.GetRequiredService<EventStoreDbContext>());
                await Assert.That(validation.IsCompatible).IsTrue();
                await Assert.That(validation.MissingTables).IsEmpty();
                await Assert.That(validation.MissingColumns).IsEmpty();
            }

            var healthy = await healthChecks.CheckHealthAsync();
            await Assert.That(healthy.Entries["eventloom.event-store"].Status).IsEqualTo(HealthStatus.Healthy);
            await Assert.That(healthy.Entries["eventloom.projections"].Status).IsEqualTo(HealthStatus.Healthy);
            await Assert.That(healthy.Entries["eventloom.outbox"].Status).IsEqualTo(HealthStatus.Healthy);
            await Assert.That(healthy.Entries["eventloom.event-store"].Data.ContainsKey("incompatible_column_count"))
                .IsFalse();

            var appended = await AppendAsync(provider);
            var delayed = await healthChecks.CheckHealthAsync();
            await Assert.That(delayed.Entries["eventloom.projections"].Status).IsEqualTo(HealthStatus.Degraded);
            await Assert.That(delayed.Entries["eventloom.projections"].Data.ContainsKey("maximum_lag")).IsTrue();
            await Assert.That(delayed.Entries["eventloom.outbox"].Status).IsEqualTo(HealthStatus.Degraded);
            await Assert.That(delayed.Entries["eventloom.outbox"].Data.ContainsKey("pending_message_count")).IsTrue();

            await using (var scope = provider.CreateAsyncScope())
            {
                var key = new ProjectionKey("tests.health", 1);
                var leaseStore = scope.ServiceProvider.GetRequiredService<WorkerLeaseStore>();
                var lease = await leaseStore.TryAcquireAsync(
                    "tenant-a",
                    ProjectionStore.GetLeaseName(key),
                    "health-test",
                    TimeSpan.FromMinutes(1));
                await scope.ServiceProvider.GetRequiredService<ProjectionStore>().RecordFailureAsync(
                    "tenant-a",
                    key,
                    appended.Events.Single(),
                    lease!,
                    1,
                    new InvalidOperationException());
            }

            var failedProjection = await healthChecks.CheckHealthAsync();
            await Assert.That(failedProjection.Entries["eventloom.projections"].Status)
                .IsEqualTo(HealthStatus.Unhealthy);
            await Assert.That(failedProjection.Entries["eventloom.projections"].Data.ContainsKey("tenant_id"))
                .IsFalse();
        }
        finally
        {
            await provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static async Task EnsureCreatedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<AppendResult> AppendAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(new AppendRequest(
            "tenant-a",
            "order-1",
            "order",
            ExpectedVersion.NoStream,
            [new ItemAdded()],
            new EventMetadata()));
    }

    [EventType("tests.health-item-added")]
    private sealed record ItemAdded : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(ItemAdded @event)
        {
        }
    }

    private sealed class RecordingProjection : IProjectionHandler<ItemAdded>
    {
        public Task HandleAsync(EventEnvelope<ItemAdded> envelope, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoopOutboxPublisher : IOutboxPublisher
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
