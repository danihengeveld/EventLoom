using EventLoom;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using EventLoom.MongoDb;
using EventLoom.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
try
{
    var services = new ServiceCollection();
    services
        .AddEventLoom()
        .UseSqlite($"Data Source={databasePath}")
        .UseSingleTenancy("consumer")
        .AddAggregateEvents<Cart>();

    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IStorageSchema>().EnsureCreatedAsync();

    var eventStore = scope.ServiceProvider.GetRequiredService<EventStore>();
    await eventStore.AppendAsync(new AppendRequest(
        "consumer",
        "cart-1",
        "cart",
        ExpectedVersion.NoStream,
        [new ItemAdded("coffee")],
        new EventMetadata()));

    var history = await eventStore.ReadStreamAsync("consumer", "cart-1");
    if (history.Count != 1 || history[0].Event is not ItemAdded { Sku: "coffee" })
    {
        throw new InvalidOperationException("The package consumer did not read the event it appended.");
    }

    // The MongoDB provider must compose from its package without opening a connection.
    var mongoServices = new ServiceCollection();
    mongoServices
        .AddEventLoom()
        .UseMongoDb("mongodb://localhost:27017/?directConnection=true", "consumer")
        .UseSingleTenancy("consumer")
        .AddAggregateEvents<Cart>();
    await using var mongoProvider = mongoServices.BuildServiceProvider();
    await using var mongoScope = mongoProvider.CreateAsyncScope();
    _ = mongoScope.ServiceProvider.GetRequiredService<EventStore>();
    if (mongoProvider.GetRequiredService<StorageCapabilities>().ProviderName != "MongoDB")
    {
        throw new InvalidOperationException("The MongoDB provider package did not register its storage.");
    }
}
finally
{
    SqliteConnection.ClearAllPools();
    File.Delete(databasePath);
    File.Delete($"{databasePath}-shm");
    File.Delete($"{databasePath}-wal");
}

internal sealed record ItemAdded(string Sku) : IDomainEvent<ItemAdded, Cart>
{
    public static string EventType => "package-consumer.item-added";
}

internal sealed class Cart(string id) : Aggregate<Cart, string>(id), IApply<ItemAdded>
{
    void IApply<ItemAdded>.Apply(ItemAdded @event)
    {
    }
}
