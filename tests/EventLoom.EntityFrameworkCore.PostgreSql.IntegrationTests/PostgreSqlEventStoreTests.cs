using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlEventStoreTests
{
    [Test]
    public async Task PostgreSql_persists_an_append_and_reads_it_back()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();

        var options = new EventStoreOptions { UseSchema = true, Schema = "eventloom_test", TablePrefix = "eventloom_" };
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>()
                .UseNpgsql(container.GetConnectionString())
                .Options,
            options);
        await context.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<OrderPlaced>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
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

    [EventType("integration.order-placed")]
    private sealed record OrderPlaced(string Sku, int Quantity) : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(OrderPlaced @event) { }
    }
}
