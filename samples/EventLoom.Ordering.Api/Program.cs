using EventLoom;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var provider = builder.Configuration["EVENTLOOM_DATABASE_PROVIDER"]?.Trim().ToLowerInvariant() ?? "postgres";
if (provider == "sqlite")
{
    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
}

var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? (provider == "sqlite" ? "Data Source=eventloom-ordering.db" : "Host=localhost;Database=eventloom;Username=eventloom;Password=eventloom");
var eventStoreOptions = new EventStoreOptions
{
    UseSchema = provider == "postgres",
    Schema = "eventloom",
    TablePrefix = "eventloom_"
};

builder.Services.AddSingleton(new EventRegistry().RegisterEvent<OrderPlaced>());
builder.Services.AddSingleton<EventSerializer>();
builder.Services.AddSingleton<IEventIdGenerator, UuidV7EventIdGenerator>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(eventStoreOptions);
builder.Services.AddDbContext<EventStoreDbContext>((services, options) =>
{
    if (provider == "sqlite")
    {
        options.UseSqlite(connectionString);
    }
    else if (provider == "postgres")
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        throw new InvalidOperationException("EVENTLOOM_DATABASE_PROVIDER must be 'postgres' or 'sqlite'.");
    }
});
builder.Services.AddScoped<EventStore>();
builder.Services.AddScoped<AggregateRepository<Order, Guid>>(services =>
    new AggregateRepository<Order, Guid>(
        services.GetRequiredService<EventStore>(),
        id => new Order(id),
        order => order.PendingEvents.Select(value => value.Event),
        order => order.Version));

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.EnsureCreatedAsync();
}

app.MapPost("/orders", async (
    PlaceOrderRequest request,
    AggregateRepository<Order, Guid> repository,
    CancellationToken cancellationToken) =>
{
    var order = new Order(Guid.NewGuid());
    order.Place(request.Sku, request.Quantity);
    await repository.SaveAsync(
        "default",
        order.Id.ToString("D"),
        "order",
        order,
        new EventMetadata(Actor: "ordering-api"),
        Guid.NewGuid().ToString("D"),
        cancellationToken);
    return Results.Created($"/orders/{order.Id:D}", new { order.Id, order.Status });
});

app.MapGet("/orders/{id:guid}", async (
    Guid id,
    AggregateRepository<Order, Guid> repository,
    CancellationToken cancellationToken) =>
{
    var order = await repository.LoadAsync("default", id.ToString("D"), id, cancellationToken);
    return order.Version == 0 ? Results.NotFound() : Results.Ok(new { order.Id, order.Status, order.Sku, order.Quantity });
});

app.Run();

public sealed record PlaceOrderRequest(string Sku, int Quantity);

[EventType("ordering.order-placed", Version = 1)]
public sealed record OrderPlaced(string Sku, int Quantity) : IDomainEvent;

public sealed class Order(Guid id) : Aggregate<Guid>(id)
{
    public string? Sku { get; private set; }
    public int Quantity { get; private set; }
    public string Status { get; private set; } = "new";

    public void Place(string sku, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        Raise(new OrderPlaced(sku, quantity));
    }

    private void Apply(OrderPlaced @event)
    {
        Sku = @event.Sku;
        Quantity = @event.Quantity;
        Status = "placed";
    }
}
