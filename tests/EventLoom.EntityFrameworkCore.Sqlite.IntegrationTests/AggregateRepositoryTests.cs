using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        var adapter = new JsonAggregateSnapshotAdapter<Counter, CounterSnapshot>(
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
        var adapter = new JsonAggregateSnapshotAdapter<Counter, CounterSnapshot>(
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
}
