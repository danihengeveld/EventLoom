using EventLoom;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
try
{
    var services = new ServiceCollection();
    services.AddEventLoom(eventLoom => eventLoom
        .RegisterEvent<ItemAdded>()
        .UseSqlite($"Data Source={databasePath}"));

    await using var provider = services.BuildServiceProvider();
    await using var scope = provider.CreateAsyncScope();
    var context = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
    await context.Database.EnsureCreatedAsync();

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
}
finally
{
    File.Delete(databasePath);
    File.Delete($"{databasePath}-shm");
    File.Delete($"{databasePath}-wal");
}

[EventType("package-consumer.item-added", Version = 1)]
internal sealed record ItemAdded(string Sku) : IDomainEvent;
