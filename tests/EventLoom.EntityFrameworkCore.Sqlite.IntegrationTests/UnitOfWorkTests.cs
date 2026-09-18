using EventLoom.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.UnitTests;

public sealed class UnitOfWorkTests
{
    [Test]
    public async Task Unit_of_work_commits_application_events_and_outbox_together()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var eventContext = new EventStoreDbContext(options, Options());
        await eventContext.Database.EnsureCreatedAsync();
        await CreateApplicationTableAsync(connection);
        var eventStore = CreateEventStore(eventContext);

        await using var applicationContext = new ApplicationDbContext(CreateApplicationOptions(connection));
        await using (var unitOfWork = await eventStore.BeginUnitOfWorkAsync())
        {
            await unitOfWork.EnlistAsync(applicationContext);
            applicationContext.Records.Add(new ApplicationRecord { Id = 1, Name = "committed" });
            await applicationContext.SaveChangesAsync();
            await unitOfWork.AppendAsync(Request([new ItemAdded(1)]));
            await unitOfWork.CommitAsync();
        }

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
    public async Task Unit_of_work_rolls_back_application_events_and_outbox_together_when_rolled_back_explicitly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var eventContext = new EventStoreDbContext(options, Options());
        await eventContext.Database.EnsureCreatedAsync();
        await CreateApplicationTableAsync(connection);
        var eventStore = CreateEventStore(eventContext);

        await using (var applicationContext = new ApplicationDbContext(CreateApplicationOptions(connection)))
        await using (var unitOfWork = await eventStore.BeginUnitOfWorkAsync())
        {
            await unitOfWork.EnlistAsync(applicationContext);
            applicationContext.Records.Add(new ApplicationRecord { Id = 1, Name = "rolled-back" });
            await applicationContext.SaveChangesAsync();
            await unitOfWork.AppendAsync(Request([new ItemAdded(1)]));
            await unitOfWork.RollbackAsync();
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
    public async Task Unit_of_work_rolls_back_when_disposed_without_committing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var eventContext = new EventStoreDbContext(options, Options());
        await eventContext.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(eventContext);

        await using (var unitOfWork = await eventStore.BeginUnitOfWorkAsync())
        {
            await unitOfWork.AppendAsync(Request([new ItemAdded(1)]));
        }

        await using var verificationContext = new EventStoreDbContext(options, Options());
        await Assert.That((await CreateEventStore(verificationContext).ReadStreamAsync("tenant-a", "order-1")).Count)
            .IsEqualTo(0);
    }

    [Test]
    public async Task Enlist_rejects_an_application_context_from_a_different_connection()
    {
        await using var eventConnection = new SqliteConnection("Data Source=:memory:");
        await eventConnection.OpenAsync();
        await using var otherConnection = new SqliteConnection("Data Source=:memory:");
        await otherConnection.OpenAsync();
        await using var eventContext = new EventStoreDbContext(CreateOptions(eventConnection), Options());
        await eventContext.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(eventContext);
        await using var applicationContext = new ApplicationDbContext(CreateApplicationOptions(otherConnection));

        await using var unitOfWork = await eventStore.BeginUnitOfWorkAsync();
        await Assert.That(async () => await unitOfWork.EnlistAsync(applicationContext))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Operations_after_commit_throw()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var eventContext = new EventStoreDbContext(CreateOptions(connection), Options());
        await eventContext.Database.EnsureCreatedAsync();
        var eventStore = CreateEventStore(eventContext);

        var unitOfWork = await eventStore.BeginUnitOfWorkAsync();
        await unitOfWork.AppendAsync(Request([new ItemAdded(1)]));
        await unitOfWork.CommitAsync();

        await Assert.That(async () => await unitOfWork.AppendAsync(Request([new ItemAdded(2)])))
            .Throws<InvalidOperationException>();
        await Assert.That(async () => await unitOfWork.CommitAsync())
            .Throws<InvalidOperationException>();
        await Assert.That(async () => await unitOfWork.RollbackAsync())
            .Throws<InvalidOperationException>();

        await unitOfWork.DisposeAsync();
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

    [EventType("tests.unit-of-work-item-added")]
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
