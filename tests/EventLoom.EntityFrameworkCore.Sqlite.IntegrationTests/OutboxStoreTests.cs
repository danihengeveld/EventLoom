using EventLoom.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.UnitTests;

public sealed class OutboxStoreTests
{
    [Test]
    public async Task Committed_append_creates_unpublished_messages_that_survive_a_new_database_connection()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        Guid[] eventIds;
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var context = new EventStoreDbContext(CreateOptions(connection), Options());
                await context.Database.EnsureCreatedAsync();
                var result = await CreateEventStore(context).AppendAsync(Request([new ItemAdded(1), new ItemAdded(2)]));

                await Assert.That(result.Events.Count).IsEqualTo(2);
                eventIds = result.Events.Select(value => value.EventId).ToArray();
            }

            await using var readConnection = new SqliteConnection($"Data Source={databasePath}");
            await readConnection.OpenAsync();
            await using var readContext = new EventStoreDbContext(CreateOptions(readConnection), Options());
            var messages = await EfTestStores.Outbox(readContext, TimeProvider.System).ReadPendingAsync("tenant-a");

            await Assert.That(messages.Count).IsEqualTo(2);
            await Assert.That(messages.Select(message => message.MessageId)).IsEquivalentTo(eventIds);
            await Assert.That(messages.Select(message => message.TenantOffset)).IsEquivalentTo(new long[] { 1, 2 });
            await Assert.That(messages.Select(message => message.AttemptCount)).IsEquivalentTo(new[] { 0, 0 });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static EventStore CreateEventStore(EventStoreDbContext context) =>
        EfTestStores.EventStore(
            context,
            new EventSerializer(new EventRegistry().RegisterAggregate<TestAggregate>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            new EventStoreOptions { OutboxEnabled = true });

    private static AppendRequest Request(IReadOnlyList<object> events) =>
        new("tenant-a", "order-1", "order", ExpectedVersion.NoStream, events, new EventMetadata());

    private static DbContextOptions<EventStoreDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options;

    private static EntityFrameworkStorageOptions Options() => new() { TablePrefix = "test_" };

    private sealed record ItemAdded(int Quantity) : IDomainEvent<ItemAdded, TestAggregate>
    {
        public static string EventType => "tests.outbox-item-added";
    }

    private sealed class TestAggregate(Guid id) : Aggregate<TestAggregate, Guid>(id), IApply<ItemAdded>
    {
        void IApply<ItemAdded>.Apply(ItemAdded @event)
        {
        }
    }
}
