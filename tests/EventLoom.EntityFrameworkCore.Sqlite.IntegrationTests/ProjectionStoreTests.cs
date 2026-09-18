using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests;

public sealed class ProjectionStoreTests
{
    [Test]
    public async Task Ef_projection_updates_and_checkpoint_commit_atomically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(context);
        var envelope = await AppendAsync(eventStore);
        var projections = new ProjectionStore(context, TimeProvider.System);
        var key = new ProjectionKey("tests.orders", 1);
        var lease = await AcquireLeaseAsync(context, key);

        var result = await projections.ProcessAsync(
            "tenant-a",
            key,
            envelope,
            lease,
            (projectionContext, _) =>
            {
                projectionContext.Set<OrderReadModel>().Add(new OrderReadModel
                {
                    TenantId = "tenant-a",
                    OrderId = envelope.StreamId,
                    Quantity = ((ItemAdded)envelope.Event).Quantity
                });
                return Task.CompletedTask;
            });

        var checkpoint = await projections.GetCheckpointAsync("tenant-a", key);
        var readModel = await context.Set<OrderReadModel>().SingleAsync();

        await Assert.That(result).IsEqualTo(ProjectionDeliveryResult.Processed);
        await Assert.That(checkpoint!.TenantOffset).IsEqualTo(envelope.TenantOffset);
        await Assert.That(readModel.Quantity).IsEqualTo(3);
    }

    [Test]
    public async Task Repeated_delivery_does_not_repeat_committed_ef_effects()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(context);
        var envelope = await AppendAsync(eventStore);
        var projections = new ProjectionStore(context, TimeProvider.System);
        var key = new ProjectionKey("tests.orders", 1);
        var lease = await AcquireLeaseAsync(context, key);
        var calls = 0;

        await projections.ProcessAsync(
            "tenant-a",
            key,
            envelope,
            lease,
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });
        var repeated = await projections.ProcessAsync(
            "tenant-a",
            key,
            envelope,
            lease,
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });

        await Assert.That(repeated).IsEqualTo(ProjectionDeliveryResult.AlreadyProcessed);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Failed_ef_projection_rolls_back_read_model_and_checkpoint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(context);
        var envelope = await AppendAsync(eventStore);
        var projections = new ProjectionStore(context, TimeProvider.System);
        var key = new ProjectionKey("tests.orders", 1);
        var lease = await AcquireLeaseAsync(context, key);

        await Assert.That(async () => await projections.ProcessAsync(
                "tenant-a",
                key,
                envelope,
                lease,
                (projectionContext, _) =>
                {
                    projectionContext.Set<OrderReadModel>().Add(new OrderReadModel
                    {
                        TenantId = "tenant-a",
                        OrderId = envelope.StreamId,
                        Quantity = 3
                    });
                    throw new InvalidOperationException("projection failed");
                }))
            .Throws<InvalidOperationException>();
        context.ChangeTracker.Clear();

        await Assert.That(await context.Set<OrderReadModel>().CountAsync()).IsEqualTo(0);
        await Assert.That(await projections.GetCheckpointAsync("tenant-a", key)).IsNull();
    }

    [Test]
    public async Task Terminal_failure_pauses_projection_and_skip_advances_checkpoint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(context);
        var envelope = await AppendAsync(eventStore);
        var projections = new ProjectionStore(context, TimeProvider.System);
        var key = new ProjectionKey("tests.orders", 1);
        var lease = await AcquireLeaseAsync(context, key);

        await projections.RecordFailureAsync(
            "tenant-a",
            key,
            envelope,
            lease,
            3,
            new InvalidOperationException("projection failed"));

        var paused = await projections.GetCheckpointAsync("tenant-a", key);
        var failures = await projections.ReadFailuresAsync("tenant-a", key);
        var skipped = await projections.SkipAsync("tenant-a", key, envelope.EventId);
        var resumed = await projections.GetCheckpointAsync("tenant-a", key);
        var resolved = await projections.ReadFailuresAsync("tenant-a", key, includeResolved: true);

        await Assert.That(paused!.Status).IsEqualTo(ProjectionStatus.Paused);
        await Assert.That(failures.Single().ExceptionType).Contains(nameof(InvalidOperationException));
        await Assert.That(skipped).IsTrue();
        await Assert.That(resumed!.Status).IsEqualTo(ProjectionStatus.Running);
        await Assert.That(resumed.TenantOffset).IsEqualTo(envelope.TenantOffset);
        await Assert.That(resolved.Single().WasSkipped).IsTrue();
    }

    [Test]
    public async Task Resume_retries_a_failed_event_and_marks_its_failure_resolved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(context);
        var envelope = await AppendAsync(eventStore);
        var projections = new ProjectionStore(context, TimeProvider.System);
        var key = new ProjectionKey("tests.orders", 1);
        var lease = await AcquireLeaseAsync(context, key);
        await projections.RecordFailureAsync(
            "tenant-a",
            key,
            envelope,
            lease,
            1,
            new InvalidOperationException("projection failed"));

        await Assert.That(await projections.ResumeAsync("tenant-a", key)).IsTrue();
        var result = await projections.ProcessAsync(
            "tenant-a",
            key,
            envelope,
            lease,
            (_, _) => Task.CompletedTask);
        var failures = await projections.ReadFailuresAsync("tenant-a", key, includeResolved: true);

        await Assert.That(result).IsEqualTo(ProjectionDeliveryResult.Processed);
        await Assert.That(failures.Single().ResolvedAt).IsNotNull();
        await Assert.That(failures.Single().WasSkipped).IsFalse();
    }

    [Test]
    public async Task Replay_resets_a_projection_checkpoint_for_a_shadow_read_model_rebuild()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(context);
        var envelope = await AppendAsync(eventStore);
        var projections = new ProjectionStore(context, TimeProvider.System);
        var key = new ProjectionKey("tests.orders", 2);
        var lease = await AcquireLeaseAsync(context, key);
        var calls = 0;

        await projections.ProcessAsync(
            "tenant-a",
            key,
            envelope,
            lease,
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });
        await projections.ReplayAsync("tenant-a", key);
        var reset = await projections.GetCheckpointAsync("tenant-a", key);
        await projections.ProcessAsync(
            "tenant-a",
            key,
            envelope,
            lease,
            (_, _) =>
            {
                calls++;
                return Task.CompletedTask;
            });

        await Assert.That(reset!.TenantOffset).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task Stale_lease_cannot_advance_a_projection_checkpoint()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(context);
        var envelope = await AppendAsync(eventStore);
        var projections = new ProjectionStore(context, TimeProvider.System);
        var key = new ProjectionKey("tests.orders", 1);
        var leases = new WorkerLeaseStore(context, TimeProvider.System);
        var stale = (await leases.TryAcquireAsync(
            "tenant-a", ProjectionStore.GetLeaseName(key), "node-a", TimeSpan.FromMinutes(1)))!;
        await leases.TryAcquireAsync("tenant-a", ProjectionStore.GetLeaseName(key), "node-a", TimeSpan.FromMinutes(1));

        await Assert.That(async () => await projections.ProcessAsync(
                "tenant-a",
                key,
                envelope,
                stale,
                (_, _) => Task.CompletedTask))
            .Throws<ProjectionLeaseLostException>();

        await Assert.That(await projections.GetCheckpointAsync("tenant-a", key)).IsNull();
    }

    private static EventStoreDbContext CreateContext(SqliteConnection connection) =>
        new(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" },
            modelBuilder =>
            {
                modelBuilder.Entity<OrderReadModel>(entity =>
                {
                    entity.ToTable("test_order_read_models");
                    entity.HasKey(value => new { value.TenantId, value.OrderId });
                });
            });

    private static EventStore CreateEventStore(EventStoreDbContext context) =>
        new(
            context,
            new EventSerializer(new EventRegistry().RegisterEvent<ItemAdded>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System);

    private static async Task<EventEnvelope> AppendAsync(EventStore eventStore) =>
        (await eventStore.AppendAsync(new AppendRequest(
            "tenant-a",
            "order-1",
            "order",
            ExpectedVersion.NoStream,
            [new ItemAdded(3)],
            new EventMetadata(CorrelationId: "correlation-1")))).Events.Single();

    private static async Task<WorkerLease> AcquireLeaseAsync(EventStoreDbContext context, ProjectionKey key) =>
        (await new WorkerLeaseStore(context, TimeProvider.System).TryAcquireAsync(
            "tenant-a",
            ProjectionStore.GetLeaseName(key),
            "node-a",
            TimeSpan.FromMinutes(1)))!;

    [EventType("tests.item-added")]
    private sealed record ItemAdded(int Quantity) : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(ItemAdded @event)
        {
        }
    }

    private sealed class OrderReadModel
    {
        public required string TenantId { get; set; }
        public required string OrderId { get; set; }
        public int Quantity { get; set; }
    }
}
