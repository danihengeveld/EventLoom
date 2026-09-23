namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlLeaseTests : PostgreSqlIntegrationTest
{
    [Test]
    public async Task Projection_tenant_discovery_and_health_use_only_committed_offsets()
    {
        await using var database = await Server.CreateDatabaseAsync();
        await using var context = database.CreateContext();
        context.TenantOffsets.Add(new TenantOffsetEntity { TenantId = "empty", NextOffset = 0 });
        await context.SaveChangesAsync();
        var projections = new ProjectionStore(context, TimeProvider.System);
        var firstKey = new ProjectionKey("tests.first", 1);
        var secondKey = new ProjectionKey("tests.second", 1);
        var empty = await projections.GetHealthSummaryAsync([firstKey, secondKey]);
        await Assert.That(await projections.ReadTenantIdsAsync()).IsEmpty();
        await Assert.That(empty.MaximumLag).IsEqualTo(0);
        await Assert.That(empty.ProjectionCount).IsEqualTo(2);
        await Assert.That((await projections.GetHealthSummaryAsync([])).ProjectionCount).IsEqualTo(0);

        var store = new EventStore(
            context,
            new EventSerializer(new EventRegistry().RegisterEvent<ItemAdded>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System);
        var first = (await store.AppendAsync(new AppendRequest(
            "tenant-a", "order-1", "order", ExpectedVersion.NoStream,
            [new ItemAdded(), new ItemAdded()], new EventMetadata()))).Events;
        await store.AppendAsync(new AppendRequest(
            "tenant-b", "order-2", "order", ExpectedVersion.NoStream,
            [new ItemAdded()], new EventMetadata()));
        var lease = (await new WorkerLeaseStore(context, TimeProvider.System).TryAcquireAsync(
            "tenant-a", ProjectionStore.GetLeaseName(firstKey), "node-a", TimeSpan.FromMinutes(1)))!;
        await projections.ProcessAsync("tenant-a", firstKey, first[0], lease, (_, _) => Task.CompletedTask);

        await Assert.That(await projections.ReadTenantIdsAsync())
            .IsEquivalentTo(new[] { "tenant-a", "tenant-b" });
        var health = await projections.GetHealthSummaryAsync([firstKey, secondKey, firstKey]);
        await Assert.That(health.ProjectionCount).IsEqualTo(2);
        await Assert.That(health.MaximumLag).IsEqualTo(2);
        await Assert.That(health.UnresolvedFailureCount).IsEqualTo(0);
    }

    [Test]
    public async Task PostgreSql_lease_fencing_rejects_a_stale_owner_release()
    {
        await using var database = await Server.CreateDatabaseAsync();
        await using var context = database.CreateContext();
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
        await using var database = await Server.CreateDatabaseAsync();
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
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
        await using var database = await Server.CreateDatabaseAsync();
        await using var context = database.CreateContext();
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
    public async Task Short_lease_expiry_allows_competing_owner_but_fences_old_projection_checkpoint()
    {
        await using var database = await Server.CreateDatabaseAsync();
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var clock = new AdjustableTimeProvider();
        var eventStore = new EventStore(
            firstContext,
            new EventSerializer(new EventRegistry().RegisterEvent<ItemAdded>()),
            new UuidV7EventIdGenerator(),
            clock);
        var envelope = (await eventStore.AppendAsync(new AppendRequest(
            "tenant-a", "order-1", "order", ExpectedVersion.NoStream,
            [new ItemAdded()], new EventMetadata()))).Events.Single();
        var key = new ProjectionKey("tests.orders", 1);
        var leaseName = ProjectionStore.GetLeaseName(key);
        var first = new WorkerLeaseStore(firstContext, clock);
        var second = new WorkerLeaseStore(secondContext, clock);
        var initial = (await first.TryAcquireAsync(
            "tenant-a", leaseName, "node-a", TimeSpan.FromMilliseconds(300)))!;

        await Assert.That(await second.TryAcquireAsync(
            "tenant-a", leaseName, "node-b", TimeSpan.FromMilliseconds(300))).IsNull();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var renewed = (await first.TryAcquireAsync(
            "tenant-a", leaseName, "node-a", TimeSpan.FromMilliseconds(300)))!;
        await Assert.That(renewed.FencingToken).IsGreaterThan(initial.FencingToken);
        await Assert.That(await second.TryAcquireAsync(
            "tenant-a", leaseName, "node-b", TimeSpan.FromMilliseconds(300))).IsNull();

        clock.Advance(TimeSpan.FromMilliseconds(301));
        var competing = (await second.TryAcquireAsync(
            "tenant-a", leaseName, "node-b", TimeSpan.FromMilliseconds(300)))!;
        await Assert.That(competing.FencingToken).IsGreaterThan(renewed.FencingToken);
        var projections = new ProjectionStore(firstContext, clock);
        await Assert.That(async () => await projections.ProcessAsync(
                "tenant-a", key, envelope, renewed, (_, _) => Task.CompletedTask))
            .Throws<ProjectionLeaseLostException>();
        await Assert.That(await projections.GetCheckpointAsync("tenant-a", key)).IsNull();
        await Assert.That(await first.ReleaseAsync(renewed)).IsFalse();
        await Assert.That(await second.ReleaseAsync(competing)).IsTrue();
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    [Test]
    public async Task Stale_postgresql_lease_cannot_record_an_outbox_delivery()
    {
        var options = new EventStoreOptions
        {
            UseSchema = true,
            Schema = "eventloom_test",
            TablePrefix = "eventloom_",
            OutboxEnabled = true
        };
        await using var database = await Server.CreateDatabaseAsync(options);
        await using var context = database.CreateContext();
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
            OutboxStore.OutboxPublisherLeaseName,
            "node-a",
            TimeSpan.FromMinutes(1)))!;
        await leases.TryAcquireAsync(
            "tenant-a",
            OutboxStore.OutboxPublisherLeaseName,
            "node-a",
            TimeSpan.FromMinutes(1));

        await Assert.That(async () => await outbox.RecordAttemptAsync(message, stale, exception: null))
            .Throws<OutboxLeaseLostException>();
        await Assert.That((await outbox.GetAsync("tenant-a", eventId))!.PublishedAt).IsNull();
    }

    [Test]
    public async Task PostgreSql_purges_successful_outbox_messages_and_attempts()
    {
        var options = new EventStoreOptions
        {
            UseSchema = true,
            Schema = "eventloom_test",
            TablePrefix = "eventloom_",
            OutboxEnabled = true
        };
        await using var database = await Server.CreateDatabaseAsync(options);
        await using var context = database.CreateContext();
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
            OutboxStore.OutboxPublisherLeaseName,
            "node-a",
            TimeSpan.FromMinutes(1)))!;

        await outbox.RecordAttemptAsync(message, lease, exception: null, TimeSpan.FromDays(1));
        await Assert.That((await outbox.GetAsync("tenant-a", eventId))!.PublishedAt).IsNotNull();

        await Assert.That(await outbox.PurgePublishedAsync(TimeSpan.Zero, 100)).IsEqualTo(1);
        await Assert.That(await outbox.GetAsync("tenant-a", eventId)).IsNull();
        await Assert.That(await outbox.ReadAttemptsAsync("tenant-a", eventId)).IsEmpty();
    }

    [EventType("tests.projection-item-added")]
    private sealed record ItemAdded : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(ItemAdded @event)
        {
        }
    }
}
