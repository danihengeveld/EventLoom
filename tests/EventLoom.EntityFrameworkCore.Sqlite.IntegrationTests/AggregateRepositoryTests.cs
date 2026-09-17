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

    [EventType("tests.incremented")]
    private sealed record Incremented(int Amount) : IDomainEvent;

    private sealed class Counter(Guid id) : Aggregate<Guid>(id)
    {
        public int Value { get; private set; }

        public void Increment(int amount) => Raise(new Incremented(amount));

        private void Apply(Incremented @event) => Value += @event.Amount;
    }
}
