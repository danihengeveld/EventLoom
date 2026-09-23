namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlEventStoreTests : PostgreSqlIntegrationTest
{
    [Test]
    public async Task PostgreSql_persists_an_append_and_reads_it_back()
    {
        await using var database = await Server.CreateDatabaseAsync();
        await using var context = database.CreateContext();

        var registry = new EventRegistry().RegisterEvent<OrderPlaced>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(),
            TimeProvider.System);
        var streamId = Guid.NewGuid().ToString("D");

        var result = await store.AppendAsync(new AppendRequest(
            "tenant-a",
            streamId,
            "order",
            ExpectedVersion.NoStream,
            [new OrderPlaced("coffee", 2)],
            new EventMetadata(Actor: "integration-test")));
        var history = await store.ReadStreamAsync("tenant-a", streamId);

        await Assert.That(result.Events.Count).IsEqualTo(1);
        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(history[0].Event).IsTypeOf<OrderPlaced>();
    }

    [Test]
    public async Task Failed_first_batch_does_not_consume_tenant_offset()
    {
        await using var database = await Server.CreateDatabaseAsync();
        await using var context = database.CreateContext();
        var registry = new EventRegistry().RegisterEvent<OrderPlaced>();
        var failedStore = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(),
            TimeProvider.System);
        await Assert.That(async () => await failedStore.AppendAsync(new AppendRequest(
                "tenant-a", "failed", new string('x', 257), ExpectedVersion.NoStream,
                [new OrderPlaced("coffee", 1)], new EventMetadata())))
            .Throws<EventStoreConcurrencyException>();

        await using var verificationContext = database.CreateContext();
        var store = new EventStore(verificationContext, new EventSerializer(registry), new UuidV7EventIdGenerator(),
            TimeProvider.System);
        var result = await store.AppendAsync(new AppendRequest(
            "tenant-a", "accepted", "order", ExpectedVersion.NoStream,
            [new OrderPlaced("coffee", 1)], new EventMetadata()));

        await Assert.That(result.Events.Single().TenantOffset).IsEqualTo(1);
        await Assert.That(await store.ReadStreamAsync("tenant-a", "failed")).IsEmpty();
    }

    [EventType("integration.order-placed")]
    private sealed record OrderPlaced(string Sku, int Quantity) : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(OrderPlaced @event)
        {
        }
    }
}
