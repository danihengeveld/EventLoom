using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlLeaseTests
{
    [Test]
    public async Task PostgreSql_lease_fencing_rejects_a_stale_owner_release()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions { UseSchema = true, Schema = "eventloom_test", TablePrefix = "eventloom_" };
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseNpgsql(container.GetConnectionString()).Options,
            options);
        await context.Database.EnsureCreatedAsync();
        var leases = new WorkerLeaseStore(context, TimeProvider.System);

        var first = await leases.TryAcquireAsync("tenant-a", "orders", "node-a", TimeSpan.FromMinutes(1));
        var renewed = await leases.TryAcquireAsync("tenant-a", "orders", "node-a", TimeSpan.FromMinutes(1));

        await Assert.That(renewed!.FencingToken).IsGreaterThan(first!.FencingToken);
        await Assert.That(await leases.ReleaseAsync(first)).IsFalse();
        await Assert.That(await leases.ReleaseAsync(renewed)).IsTrue();
    }

    [Test]
    public async Task Independent_instances_do_not_acquire_the_same_active_lease()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions { UseSchema = true, Schema = "eventloom_test", TablePrefix = "eventloom_" };
        await using var setupContext = CreateContext(container.GetConnectionString(), options);
        await setupContext.Database.EnsureCreatedAsync();
        await using var firstContext = CreateContext(container.GetConnectionString(), options);
        await using var secondContext = CreateContext(container.GetConnectionString(), options);
        var first = new WorkerLeaseStore(firstContext, TimeProvider.System);
        var second = new WorkerLeaseStore(secondContext, TimeProvider.System);

        var acquisitions = await Task.WhenAll(
            first.TryAcquireAsync("tenant-a", "orders", "node-a", TimeSpan.FromMinutes(1)),
            second.TryAcquireAsync("tenant-a", "orders", "node-b", TimeSpan.FromMinutes(1)));

        await Assert.That(acquisitions.Count(value => value is not null)).IsEqualTo(1);
        await Assert.That(acquisitions.Count(value => value is null)).IsEqualTo(1);
    }

    [Test]
    public async Task Stale_postgresql_lease_cannot_commit_a_projection_checkpoint()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions { UseSchema = true, Schema = "eventloom_test", TablePrefix = "eventloom_" };
        await using var context = CreateContext(container.GetConnectionString(), options);
        await context.Database.EnsureCreatedAsync();
        var eventStore = new EventStore(
            context,
            new EventSerializer(new EventRegistry().RegisterEvent<ItemAdded>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System);
        var envelope = (await eventStore.AppendAsync(new AppendRequest(
            "tenant-a",
            "order-1",
            "order",
            ExpectedVersion.NoStream,
            [new ItemAdded()],
            new EventMetadata()))).Events.Single();
        var key = new ProjectionKey("tests.orders", 1);
        var leases = new WorkerLeaseStore(context, TimeProvider.System);
        var stale = (await leases.TryAcquireAsync(
            "tenant-a",
            ProjectionStore.GetLeaseName(key),
            "node-a",
            TimeSpan.FromMinutes(1)))!;
        await leases.TryAcquireAsync(
            "tenant-a",
            ProjectionStore.GetLeaseName(key),
            "node-a",
            TimeSpan.FromMinutes(1));
        var projections = new ProjectionStore(context, TimeProvider.System);

        await Assert.That(async () => await projections.ProcessAsync(
                "tenant-a",
                key,
                envelope,
                stale,
                (_, _) => Task.CompletedTask))
            .Throws<ProjectionLeaseLostException>();

        await Assert.That(await projections.GetCheckpointAsync("tenant-a", key)).IsNull();
    }

    [Test]
    public async Task Stale_postgresql_lease_cannot_record_an_outbox_delivery()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions
        {
            UseSchema = true,
            Schema = "eventloom_test",
            TablePrefix = "eventloom_",
            OutboxEnabled = true
        };
        await using var context = CreateContext(container.GetConnectionString(), options);
        await context.Database.EnsureCreatedAsync();
        var eventStore = new EventStore(
            context,
            new EventSerializer(new EventRegistry().RegisterEvent<ItemAdded>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            options);
        var eventId = (await eventStore.AppendAsync(new AppendRequest(
            "tenant-a",
            "order-1",
            "order",
            ExpectedVersion.NoStream,
            [new ItemAdded()],
            new EventMetadata()))).Events.Single().EventId;
        var outbox = new OutboxStore(context, TimeProvider.System);
        var message = (await outbox.ReadPendingAsync("tenant-a")).Single();
        var leases = new WorkerLeaseStore(context, TimeProvider.System);
        var stale = (await leases.TryAcquireAsync(
            "tenant-a",
            OutboxStore.GetLeaseName(),
            "node-a",
            TimeSpan.FromMinutes(1)))!;
        await leases.TryAcquireAsync(
            "tenant-a",
            OutboxStore.GetLeaseName(),
            "node-a",
            TimeSpan.FromMinutes(1));

        await Assert.That(async () => await outbox.RecordAttemptAsync(message, stale, exception: null))
            .Throws<OutboxLeaseLostException>();
        await Assert.That((await outbox.GetAsync("tenant-a", eventId))!.PublishedAt).IsNull();
    }

    [Test]
    public async Task PostgreSql_purges_successful_outbox_messages_and_attempts()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions
        {
            UseSchema = true,
            Schema = "eventloom_test",
            TablePrefix = "eventloom_",
            OutboxEnabled = true
        };
        await using var context = CreateContext(container.GetConnectionString(), options);
        await context.Database.EnsureCreatedAsync();
        var eventStore = new EventStore(
            context,
            new EventSerializer(new EventRegistry().RegisterEvent<ItemAdded>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            options);
        var eventId = (await eventStore.AppendAsync(new AppendRequest(
            "tenant-a",
            "order-1",
            "order",
            ExpectedVersion.NoStream,
            [new ItemAdded()],
            new EventMetadata()))).Events.Single().EventId;
        var outbox = new OutboxStore(context, TimeProvider.System);
        var message = (await outbox.ReadPendingAsync("tenant-a")).Single();
        var lease = (await new WorkerLeaseStore(context, TimeProvider.System).TryAcquireAsync(
            "tenant-a",
            OutboxStore.GetLeaseName(),
            "node-a",
            TimeSpan.FromMinutes(1)))!;

        await outbox.RecordAttemptAsync(message, lease, exception: null, TimeSpan.FromDays(1));
        await Assert.That((await outbox.GetAsync("tenant-a", eventId))!.PublishedAt).IsNotNull();

        await Assert.That(await outbox.PurgePublishedAsync(TimeSpan.Zero, 100)).IsEqualTo(1);
        await Assert.That(await outbox.GetAsync("tenant-a", eventId)).IsNull();
        await Assert.That(await outbox.ReadAttemptsAsync("tenant-a", eventId)).IsEmpty();
    }

    private static EventStoreDbContext CreateContext(string connectionString, EventStoreOptions options) =>
        new(new DbContextOptionsBuilder<EventStoreDbContext>().UseNpgsql(connectionString).Options, options);

    [EventType("tests.projection-item-added")]
    private sealed record ItemAdded : IDomainEvent;
}
