using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.UnitTests;

public sealed class EventStoreAppendTests
{
    [Test]
    public async Task Append_assigns_consecutive_versions_and_reads_history()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var registry = new EventRegistry().RegisterEvent<Added>();
        var store = new EventStore(
            context,
            new EventSerializer(registry),
            new UuidV7EventIdGenerator(),
            TimeProvider.System);

        var result = await store.AppendAsync(new AppendRequest(
            "tenant-a",
            "cart-1",
            "cart",
            ExpectedVersion.NoStream,
            [new Added(1), new Added(2)],
            new EventMetadata(),
            "append-1"));
        var history = await store.ReadStreamAsync("tenant-a", "cart-1");

        await Assert.That(result.Events.Select(value => value.StreamVersion)).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(history.Count).IsEqualTo(2);
        await Assert.That(history[1].GlobalPosition).IsEqualTo(2);
    }

    [Test]
    public async Task Wrong_expected_version_rolls_back_the_batch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var registry = new EventRegistry().RegisterEvent<Added>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        await store.AppendAsync(new AppendRequest(
            "tenant-a", "cart-1", "cart", ExpectedVersion.NoStream, [new Added(1)], new EventMetadata()));

        await Assert.That(async () => await store.AppendAsync(new AppendRequest(
                "tenant-a", "cart-1", "cart", ExpectedVersion.NoStream, [new Added(2)], new EventMetadata())))
            .Throws<WrongExpectedVersionException>();
        await Assert.That((await store.ReadStreamAsync("tenant-a", "cart-1")).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Append_id_replays_existing_result()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var registry = new EventRegistry().RegisterEvent<Added>();
        var store = new EventStore(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);
        var request = new AppendRequest(
            "tenant-a", "cart-1", "cart", ExpectedVersion.NoStream, [new Added(1)], new EventMetadata(), "same");
        await store.AppendAsync(request);

        var replay = await store.AppendAsync(request);

        await Assert.That(replay.WasIdempotentReplay).IsTrue();
        await Assert.That((await store.ReadStreamAsync("tenant-a", "cart-1")).Count).IsEqualTo(1);
    }

    private static EventStoreDbContext CreateContext(SqliteConnection connection) =>
        new(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });

    [EventType("tests.added")]
    private sealed record Added(int Amount) : IDomainEvent;
}
