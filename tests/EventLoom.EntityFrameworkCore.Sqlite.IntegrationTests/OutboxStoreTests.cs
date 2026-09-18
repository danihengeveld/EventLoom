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
            var messages = await new OutboxStore(readContext, TimeProvider.System).ReadPendingAsync("tenant-a");

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

    [Test]
    public async Task Append_in_shared_transaction_commits_application_events_and_outbox_together()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var eventContext = new EventStoreDbContext(options, Options());
        await eventContext.Database.EnsureCreatedAsync();
        await CreateApplicationTableAsync(connection);
        var eventStore = CreateEventStore(eventContext);

        await using var transaction = await connection.BeginTransactionAsync();
        await using var applicationContext = new ApplicationDbContext(CreateApplicationOptions(connection));
        await applicationContext.Database.UseTransactionAsync(transaction);
        applicationContext.Records.Add(new ApplicationRecord { Id = 1, Name = "committed" });
        await applicationContext.SaveChangesAsync();
        await eventStore.AppendInTransactionAsync(Request([new ItemAdded(1)]), transaction);
        await transaction.CommitAsync();

        await using var verificationContext = new EventStoreDbContext(options, Options());
        await using var verificationApplicationContext = new ApplicationDbContext(CreateApplicationOptions(connection));
        await Assert.That((await CreateEventStore(verificationContext).ReadStreamAsync("tenant-a", "order-1")).Count)
            .IsEqualTo(1);
        await Assert.That((await new OutboxStore(verificationContext, TimeProvider.System).ReadPendingAsync("tenant-a"))
                .Count)
            .IsEqualTo(1);
        await Assert.That((await verificationApplicationContext.Records.SingleAsync()).Name).IsEqualTo("committed");
    }

    [Test]
    public async Task Append_in_shared_transaction_rolls_back_application_events_and_outbox_together()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var eventContext = new EventStoreDbContext(options, Options());
        await eventContext.Database.EnsureCreatedAsync();
        await CreateApplicationTableAsync(connection);
        var eventStore = CreateEventStore(eventContext);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var applicationContext = new ApplicationDbContext(CreateApplicationOptions(connection));
            await applicationContext.Database.UseTransactionAsync(transaction);
            applicationContext.Records.Add(new ApplicationRecord { Id = 1, Name = "rolled-back" });
            await applicationContext.SaveChangesAsync();
            await eventStore.AppendInTransactionAsync(Request([new ItemAdded(1)]), transaction);
            await transaction.RollbackAsync();
        }

        await using var verificationContext = new EventStoreDbContext(options, Options());
        await using var verificationApplicationContext = new ApplicationDbContext(CreateApplicationOptions(connection));
        await Assert.That((await CreateEventStore(verificationContext).ReadStreamAsync("tenant-a", "order-1")).Count)
            .IsEqualTo(0);
        await Assert.That((await new OutboxStore(verificationContext, TimeProvider.System).ReadPendingAsync("tenant-a"))
                .Count)
            .IsEqualTo(0);
        await Assert.That(await verificationApplicationContext.Records.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task Append_in_transaction_rejects_a_transaction_from_another_connection()
    {
        await using var eventConnection = new SqliteConnection("Data Source=:memory:");
        await eventConnection.OpenAsync();
        await using var otherConnection = new SqliteConnection("Data Source=:memory:");
        await otherConnection.OpenAsync();
        await using var eventContext = new EventStoreDbContext(CreateOptions(eventConnection), Options());
        await eventContext.Database.EnsureCreatedAsync();
        var store = CreateEventStore(eventContext);
        await using var transaction = await otherConnection.BeginTransactionAsync();

        await Assert.That(async () => await store.AppendInTransactionAsync(Request([new ItemAdded(1)]), transaction))
            .Throws<InvalidOperationException>();
    }

    private static EventStore CreateEventStore(EventStoreDbContext context) =>
        new(
            context,
            new EventSerializer(new EventRegistry().RegisterEvent<ItemAdded>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            context.Configuration);

    private static AppendRequest Request(IReadOnlyList<object> events) =>
        new("tenant-a", "order-1", "order", ExpectedVersion.NoStream, events, new EventMetadata());

    private static DbContextOptions<EventStoreDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options;

    private static DbContextOptions<ApplicationDbContext> CreateApplicationOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;

    private static EventStoreOptions Options() => new() { TablePrefix = "test_", OutboxEnabled = true };

    private static async Task CreateApplicationTableAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """CREATE TABLE "application_records" ("Id" INTEGER NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL)""";
        await command.ExecuteNonQueryAsync();
    }

    [EventType("tests.outbox-item-added")]
    private sealed record ItemAdded(int Quantity) : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(ItemAdded @event)
        {
        }
    }

    private sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
    {
        public DbSet<ApplicationRecord> Records => Set<ApplicationRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<ApplicationRecord>().ToTable("application_records");
    }

    private sealed class ApplicationRecord
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }
}
