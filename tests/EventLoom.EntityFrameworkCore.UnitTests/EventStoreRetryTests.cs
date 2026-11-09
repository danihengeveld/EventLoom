using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SQLitePCL;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class EventStoreRetryTests
{
    [Test]
    public async Task Retry_after_a_lost_commit_acknowledgement_does_not_duplicate_events()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        await using var provider = CreateProvider(databasePath);

        try
        {
            await EnsureCreatedAsync(provider);
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<EventStore>();

            var result = await store.AppendAsync(Request(ExpectedVersion.Any));
            var stored = await store.ReadStreamAsync("tenant-a", "order-1");

            await Assert.That(result.WasIdempotentReplay).IsTrue();
            await Assert.That(stored.Count).IsEqualTo(1);
            await Assert.That(result.Events.Single().EventId).IsEqualTo(stored.Single().EventId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Test]
    public async Task Retry_after_a_lost_commit_acknowledgement_does_not_report_a_false_version_conflict()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        await using var provider = CreateProvider(databasePath);

        try
        {
            await EnsureCreatedAsync(provider);
            await using var scope = provider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<EventStore>();

            var result = await store.AppendAsync(Request(ExpectedVersion.NoStream));
            var stored = await store.ReadStreamAsync("tenant-a", "order-1");

            await Assert.That(result.Events.Single().StreamVersion).IsEqualTo(1);
            await Assert.That(result.Events.Single().Event).IsEqualTo(new ItemAdded(3));
            await Assert.That(stored.Count).IsEqualTo(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static ServiceProvider CreateProvider(string databasePath)
    {
        Batteries_V2.Init();
        var services = new ServiceCollection();
        services.AddEventLoom(eventLoom => eventLoom
            .AddAggregateEvents<TestAggregate>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<IEventStoreRetryPolicy, LostAcknowledgementRetryPolicy>();
        return services.BuildServiceProvider();
    }

    private static async Task EnsureCreatedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.EnsureCreatedAsync();
    }

    private static AppendRequest Request(ExpectedVersion expectedVersion) =>
        new("tenant-a", "order-1", "order", expectedVersion, [new ItemAdded(3)], new EventMetadata());

    /// <summary>Lets the first attempt commit, then discards its result and retries, as after a lost acknowledgement.</summary>
    private sealed class LostAcknowledgementRetryPolicy : IEventStoreRetryPolicy
    {
        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            _ = await operation(cancellationToken);
            return await operation(cancellationToken);
        }
    }

    private sealed record ItemAdded(int Quantity) : IDomainEvent<ItemAdded, TestAggregate>
    {
        public static string EventType => "tests.retry-item-added";
    }

    private sealed class TestAggregate(Guid id) : Aggregate<TestAggregate, Guid>(id), IApply<ItemAdded>
    {
        void IApply<ItemAdded>.Apply(ItemAdded @event)
        {
        }
    }
}
