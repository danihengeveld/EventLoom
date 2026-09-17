using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace EventLoom.UnitTests;

public sealed class AggregateRepositoryTests
{
    [Test]
    public async Task Aggregate_repository_saves_and_reloads_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Incremented>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        var repository = new AggregateRepository<Counter, Guid>(
            store,
            id => new Counter(id),
            aggregate => aggregate.PendingEvents.Select(value => value.Event),
            aggregate => aggregate.Version);
        var aggregate = new Counter(Guid.NewGuid());
        aggregate.Increment(3);

        await repository.SaveAsync("tenant-a", aggregate.Id.ToString(), "counter", aggregate, new EventMetadata());
        await Assert.That(aggregate.PendingEvents.Count).IsEqualTo(0);
        await repository.SaveAsync("tenant-a", aggregate.Id.ToString(), "counter", aggregate, new EventMetadata());
        var loaded = await repository.LoadAsync("tenant-a", aggregate.Id.ToString(), aggregate.Id);

        await Assert.That(loaded.Value).IsEqualTo(3);
        await Assert.That(loaded.PendingEvents.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Configured_repository_uses_short_tenant_scoped_operations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Incremented>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        var repository = new AggregateRepository<Counter, Guid>(
            store,
            id => new Counter(id),
            "counter",
            id => id.ToString("D"),
            new TestTenantAccessor("tenant-a"));
        var aggregate = new Counter(Guid.NewGuid());
        aggregate.Increment(4);

        await repository.SaveAsync(aggregate);
        var loaded = await repository.LoadAsync(aggregate.Id);

        await Assert.That(loaded.Value).IsEqualTo(4);
    }

    [Test]
    public async Task Configured_repository_restores_latest_snapshot_and_replays_tail_events()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Incremented>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        var snapshots = new SnapshotStore(context, TimeProvider.System);
        var adapter = new AggregateSnapshotAdapter<Counter, CounterSnapshot>(
            aggregate => new CounterSnapshot(aggregate.Value),
            (aggregate, snapshot) => aggregate.Restore(snapshot));
        var repository = new AggregateRepository<Counter, Guid>(
            store,
            id => new Counter(id),
            "counter",
            id => id.ToString("D"),
            new TestTenantAccessor("tenant-a"),
            snapshots,
            adapter,
            new EveryNEventsSnapshotPolicy(2));
        var aggregate = new Counter(Guid.NewGuid());

        aggregate.Increment(2);
        await repository.SaveAsync(aggregate);
        aggregate.Increment(3);
        await repository.SaveAsync(aggregate);
        aggregate.Increment(4);
        await repository.SaveAsync(aggregate);

        var snapshot = await snapshots.ReadLatestAsync(
            "tenant-a",
            aggregate.Id.ToString("D"),
            "counter",
            "tests.counter");
        var loaded = await repository.LoadAsync(aggregate.Id);

        await Assert.That(snapshot!.StreamVersion).IsEqualTo(2);
        await Assert.That(loaded.Value).IsEqualTo(9);
        await Assert.That(loaded.Version).IsEqualTo(3);
    }

    [Test]
    public async Task Corrupt_snapshot_falls_back_to_full_event_replay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Incremented>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        var snapshots = new SnapshotStore(context, TimeProvider.System);
        var adapter = new AggregateSnapshotAdapter<Counter, CounterSnapshot>(
            aggregate => new CounterSnapshot(aggregate.Value),
            (aggregate, snapshot) => aggregate.Restore(snapshot));
        var repository = new AggregateRepository<Counter, Guid>(
            store,
            id => new Counter(id),
            "counter",
            id => id.ToString("D"),
            new TestTenantAccessor("tenant-a"),
            snapshots,
            adapter,
            new EveryNEventsSnapshotPolicy(100));
        var aggregate = new Counter(Guid.NewGuid());
        aggregate.Increment(2);
        await repository.SaveAsync(aggregate);
        aggregate.Increment(3);
        await repository.SaveAsync(aggregate);
        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-a",
            aggregate.Id.ToString("D"),
            "counter",
            2,
            "tests.counter",
            1,
            "{corrupt"));

        var loaded = await repository.LoadAsync(aggregate.Id);

        await Assert.That(loaded.Value).IsEqualTo(5);
        await Assert.That(loaded.Version).IsEqualTo(2);
    }

    [Test]
    public async Task Future_snapshot_version_falls_back_to_full_event_replay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Incremented>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        var snapshots = new SnapshotStore(context, TimeProvider.System);
        var adapter = new AggregateSnapshotAdapter<Counter, CounterSnapshot>(
            aggregate => new CounterSnapshot(aggregate.Value),
            (aggregate, snapshot) => aggregate.Restore(snapshot));
        var repository = new AggregateRepository<Counter, Guid>(
            store,
            id => new Counter(id),
            "counter",
            id => id.ToString("D"),
            new TestTenantAccessor("tenant-a"),
            snapshots,
            adapter);
        var aggregate = new Counter(Guid.NewGuid());
        aggregate.Increment(5);
        await repository.SaveAsync(aggregate);
        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-a",
            aggregate.Id.ToString("D"),
            "counter",
            1,
            "tests.counter",
            2,
            """{"value":500}"""));

        var loaded = await repository.LoadAsync(aggregate.Id);

        await Assert.That(loaded.Value).IsEqualTo(5);
        await Assert.That(loaded.Version).IsEqualTo(1);
    }

    [Test]
    public async Task Configured_invalidator_removes_an_unusable_snapshot_after_full_replay()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Incremented>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        var snapshots = new SnapshotStore(context, TimeProvider.System);
        var adapter = new AggregateSnapshotAdapter<Counter, CounterSnapshot>(
            aggregate => new CounterSnapshot(aggregate.Value),
            (aggregate, snapshot) => aggregate.Restore(snapshot));
        var repository = new AggregateRepository<Counter, Guid>(
            store,
            id => new Counter(id),
            "counter",
            id => id.ToString("D"),
            new TestTenantAccessor("tenant-a"),
            snapshots,
            adapter,
            snapshotInvalidator: new AlwaysInvalidateSnapshots());
        var aggregate = new Counter(Guid.NewGuid());
        aggregate.Increment(5);
        await repository.SaveAsync(aggregate);
        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-a",
            aggregate.Id.ToString("D"),
            "counter",
            1,
            "tests.counter",
            1,
            "{corrupt"));

        var loaded = await repository.LoadAsync(aggregate.Id);
        var snapshot = await snapshots.ReadLatestAsync(
            "tenant-a",
            aggregate.Id.ToString("D"),
            "counter",
            "tests.counter");

        await Assert.That(loaded.Value).IsEqualTo(5);
        await Assert.That(snapshot).IsNull();
    }

    [Test]
    public async Task Snapshot_retention_keeps_the_latest_snapshot_per_tenant_and_stream()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();
        var snapshots = new SnapshotStore(context, TimeProvider.System);

        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-a", "counter-1", "counter", 1, "tests.counter", 1, """{"value":1}"""));
        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-a", "counter-1", "counter", 2, "tests.counter", 1, """{"value":2}"""));
        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-b", "counter-1", "counter", 1, "tests.counter", 1, """{"value":3}"""));

        var firstTenant = await snapshots.ReadLatestAsync("tenant-a", "counter-1", "counter", "tests.counter");
        var secondTenant = await snapshots.ReadLatestAsync("tenant-b", "counter-1", "counter", "tests.counter");

        await Assert.That(firstTenant!.StreamVersion).IsEqualTo(2);
        await Assert.That(secondTenant!.StreamVersion).IsEqualTo(1);
    }

    [Test]
    public async Task Configured_retention_policy_keeps_the_requested_recent_snapshots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();
        var snapshots = new SnapshotStore(context, TimeProvider.System, new KeepLatestSnapshotsPolicy(2));

        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-a", "counter-1", "counter", 1, "tests.counter", 1, """{"value":1}"""));
        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-a", "counter-1", "counter", 2, "tests.counter", 1, """{"value":2}"""));
        await snapshots.WriteAsync(new SnapshotWriteRequest(
            "tenant-a", "counter-1", "counter", 3, "tests.counter", 1, """{"value":3}"""));

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "StreamVersion"
            FROM "test_snapshots"
            WHERE "TenantId" = 'tenant-a' AND "StreamId" = 'counter-1'
            ORDER BY "StreamVersion";
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var versions = new List<long>();
        while (await reader.ReadAsync())
        {
            versions.Add(reader.GetInt64(0));
        }

        await Assert.That(versions).IsEquivalentTo(new long[] { 2, 3 });
    }

    [Test]
    public async Task Snapshot_write_failure_does_not_undo_committed_events()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(new FailingSnapshotInsertInterceptor())
                .Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Incremented>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        var adapter = new AggregateSnapshotAdapter<Counter, CounterSnapshot>(
            aggregate => new CounterSnapshot(aggregate.Value),
            (aggregate, snapshot) => aggregate.Restore(snapshot));
        var repository = new AggregateRepository<Counter, Guid>(
            store,
            id => new Counter(id),
            "counter",
            id => id.ToString("D"),
            new TestTenantAccessor("tenant-a"),
            new SnapshotStore(context, TimeProvider.System),
            adapter,
            new EveryNEventsSnapshotPolicy(1));
        var aggregate = new Counter(Guid.NewGuid());
        aggregate.Increment(5);

        await Assert.That(async () => await repository.SaveAsync(aggregate))
            .Throws<DbUpdateException>();
        var history = await store.ReadStreamAsync("tenant-a", aggregate.Id.ToString("D"));

        await Assert.That(history.Count).IsEqualTo(1);
        await Assert.That(aggregate.PendingEvents).IsEmpty();
    }

    [EventType("tests.incremented")]
    private sealed record Incremented(int Amount) : IDomainEvent;

    private sealed class Counter(Guid id) : Aggregate<Guid>(id)
    {
        public int Value { get; private set; }

        public void Increment(int amount) => Raise(new Incremented(amount));

        private void Apply(Incremented @event) => Value += @event.Amount;

        public void Restore(CounterSnapshot snapshot) => Value = snapshot.Value;
    }

    [SnapshotType("tests.counter", Version = 1)]
    private sealed record CounterSnapshot(int Value) : IAggregateSnapshot;

    private sealed class TestTenantAccessor(string tenant) : ITenantAccessor
    {
        public TenantId? TenantId { get; } = new(tenant);
    }

    private sealed class AlwaysInvalidateSnapshots : ISnapshotInvalidator
    {
        public bool ShouldInvalidate(
            string snapshotType,
            int schemaVersion,
            SnapshotInvalidationReason reason) => true;
    }

    private sealed class FailingSnapshotInsertInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"test_snapshots\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Snapshot write failed.");
            }

            return ValueTask.FromResult(result);
        }
    }
}
