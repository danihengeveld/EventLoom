using EventLoom;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
var provider = builder.Configuration["EVENTLOOM_DATABASE_PROVIDER"]?.Trim().ToLowerInvariant() ?? "postgres";
var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? (provider == "sqlite"
        ? "Data Source=eventloom-ordering.db"
        : "Host=localhost;Database=eventloom;Username=eventloom;Password=eventloom");
var orderSnapshots = new AggregateSnapshotAdapter<Order, OrderSnapshot>(
    order => new OrderSnapshot(order.Status, order.Items.ToArray()),
    (order, snapshot) => order.Restore(snapshot));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantAccessor, RequestTenantAccessor>();
builder.Services.AddEventLoom(eventLoom =>
{
    eventLoom.ConfigureTenancy(TenancyMode.Required);
    eventLoom.RegisterEvent<OrderPlaced>();
    eventLoom.RegisterEvent<OrderItemAdded>();
    eventLoom.RegisterEvent<OrderCancelled>();
    eventLoom.ConfigureProjectionModel(modelBuilder =>
    {
        modelBuilder.Entity<OrderSummary>(entity =>
        {
            entity.ToTable("ordering_order_summaries");
            entity.HasKey(value => new { value.TenantId, value.OrderId });
        });
    });
    eventLoom.AddAggregateRepository<Order, Guid>(
        id => new Order(id),
        "order",
        id => id.ToString("D"),
        orderSnapshots,
        new EveryNEventsSnapshotPolicy(2));
    eventLoom.AddEfProjection<OrderSummaryProjection, OrderPlaced>("ordering.order-summary");
    eventLoom.AddEfProjection<OrderSummaryProjection, OrderItemAdded>("ordering.order-summary");
    eventLoom.AddEfProjection<OrderSummaryProjection, OrderCancelled>("ordering.order-summary");

    if (provider == "sqlite")
    {
        eventLoom.UseSqlite(connectionString);
    }
    else if (provider == "postgres")
    {
        eventLoom.UsePostgreSql(connectionString);
    }
    else
    {
        throw new InvalidOperationException("EVENTLOOM_DATABASE_PROVIDER must be 'postgres' or 'sqlite'.");
    }
});

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.EnsureCreatedAsync();
}

app.Use(async (context, next) =>
{
    if (!context.Request.Headers.TryGetValue("X-Tenant-ID", out var tenant) ||
        string.IsNullOrWhiteSpace(tenant))
    {
        await Results.BadRequest(new { error = "The X-Tenant-ID request header is required." })
            .ExecuteAsync(context);
        return;
    }

    await next(context);
});

app.MapPost("/orders", async (
    PlaceOrderRequest request,
    AggregateRepository<Order, Guid> repository,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var order = new Order(request.OrderId ?? Guid.NewGuid());
    try
    {
        order.Place(request.Sku, request.Quantity);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }

    await repository.SaveAsync(
        order,
        RequestMetadata(context),
        context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
        cancellationToken);
    return Results.Created($"/orders/{order.Id:D}", new { order.Id, order.Status, order.Items });
});

app.MapGet("/orders/{id:guid}", async (
    Guid id,
    AggregateRepository<Order, Guid> repository,
    CancellationToken cancellationToken) =>
{
    var order = await repository.LoadAsync(id, cancellationToken);
    return order.Version == 0
        ? Results.NotFound()
        : Results.Ok(new { order.Id, order.Status, order.Items });
});

app.MapPost("/orders/{id:guid}/items", async (
    Guid id,
    AddOrderItemRequest request,
    AggregateRepository<Order, Guid> repository,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var order = await repository.LoadAsync(id, cancellationToken);
    if (order.Version == 0)
    {
        return Results.NotFound();
    }

    try
    {
        order.AddItem(request.Sku, request.Quantity);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }

    await repository.SaveAsync(
        order,
        RequestMetadata(context),
        context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
        cancellationToken);
    return Results.Ok(new { order.Id, order.Status, order.Items });
});

app.MapPost("/orders/{id:guid}/cancel", async (
    Guid id,
    CancelOrderRequest request,
    AggregateRepository<Order, Guid> repository,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    var order = await repository.LoadAsync(id, cancellationToken);
    if (order.Version == 0)
    {
        return Results.NotFound();
    }

    try
    {
        order.Cancel(request.Reason);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }

    await repository.SaveAsync(
        order,
        RequestMetadata(context),
        context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
        cancellationToken);
    return Results.Ok(new { order.Id, order.Status });
});

app.MapGet("/orders/{id:guid}/events", async (
    Guid id,
    EventStore store,
    ITenantAccessor tenantAccessor,
    CancellationToken cancellationToken) =>
{
    var history = await store.ReadStreamAsync(
        tenantAccessor.TenantId!.Value.Value,
        id.ToString("D"),
        cancellationToken: cancellationToken);
    return Results.Ok(history.Select(envelope => new
    {
        envelope.EventId,
        envelope.EventType,
        envelope.EventTypeVersion,
        envelope.StreamVersion,
        envelope.TenantOffset,
        envelope.OccurredAt,
        envelope.Metadata
    }));
});

app.MapGet("/orders/{id:guid}/summary", async (
    Guid id,
    EventStoreDbContext context,
    ITenantAccessor tenantAccessor,
    CancellationToken cancellationToken) =>
{
    var summary = await context.Set<OrderSummary>().SingleOrDefaultAsync(
        value => value.TenantId == tenantAccessor.TenantId!.Value.Value && value.OrderId == id,
        cancellationToken);
    return summary is null
        ? Results.NotFound()
        : Results.Ok(summary);
});

app.MapGet("/projections/order-summary", async (
    ProjectionAdministration administration,
    ITenantAccessor tenantAccessor,
    CancellationToken cancellationToken) =>
{
    var key = new ProjectionKey("ordering.order-summary", 1);
    var tenantId = tenantAccessor.TenantId!.Value.Value;
    var checkpoint = await administration.GetCheckpointAsync(tenantId, key, cancellationToken);
    var failures = await administration.ReadFailuresAsync(tenantId, key, cancellationToken: cancellationToken);
    return Results.Ok(new { checkpoint, failures });
});

app.Run();

static EventMetadata RequestMetadata(HttpContext context) =>
    new(
        CorrelationId: context.Request.Headers["X-Correlation-ID"].FirstOrDefault(),
        Actor: "ordering-api");

internal sealed record PlaceOrderRequest(string Sku, int Quantity, Guid? OrderId = null);

internal sealed record AddOrderItemRequest(string Sku, int Quantity);

internal sealed record CancelOrderRequest(string Reason);

internal sealed class RequestTenantAccessor(IHttpContextAccessor httpContextAccessor) : ITenantAccessor
{
    public TenantId? TenantId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.Request.Headers["X-Tenant-ID"].FirstOrDefault();
            return string.IsNullOrWhiteSpace(value) ? null : new TenantId(value);
        }
    }
}

[EventType("ordering.order-placed", Version = 1)]
internal sealed record OrderPlaced(string Sku, int Quantity) : IDomainEvent;

[EventType("ordering.order-item-added", Version = 1)]
internal sealed record OrderItemAdded(string Sku, int Quantity) : IDomainEvent;

[EventType("ordering.order-cancelled", Version = 1)]
internal sealed record OrderCancelled(string Reason) : IDomainEvent;

internal sealed record OrderItem(string Sku, int Quantity);

[SnapshotType("ordering.order", Version = 1)]
internal sealed record OrderSnapshot(string Status, IReadOnlyList<OrderItem> Items) : IAggregateSnapshot;

internal sealed class OrderSummary
{
    public required string TenantId { get; set; }
    public Guid OrderId { get; set; }
    public required string Status { get; set; }
    public int ItemCount { get; set; }
    public int TotalQuantity { get; set; }
    public long TenantOffset { get; set; }
}

internal sealed class OrderSummaryProjection :
    IEfProjectionHandler<OrderPlaced>,
    IEfProjectionHandler<OrderItemAdded>,
    IEfProjectionHandler<OrderCancelled>
{
    public Task HandleAsync(
        EventEnvelope<OrderPlaced> envelope,
        EventStoreDbContext context,
        CancellationToken cancellationToken)
    {
        context.Set<OrderSummary>().Add(new OrderSummary
        {
            TenantId = Tenant(envelope),
            OrderId = Guid.Parse(envelope.StreamId),
            Status = "active",
            ItemCount = 1,
            TotalQuantity = envelope.Event.Quantity,
            TenantOffset = envelope.TenantOffset
        });
        return Task.CompletedTask;
    }

    public async Task HandleAsync(
        EventEnvelope<OrderItemAdded> envelope,
        EventStoreDbContext context,
        CancellationToken cancellationToken)
    {
        var summary = await FindAsync(context, envelope, cancellationToken);
        summary.ItemCount++;
        summary.TotalQuantity += envelope.Event.Quantity;
        summary.TenantOffset = envelope.TenantOffset;
    }

    public async Task HandleAsync(
        EventEnvelope<OrderCancelled> envelope,
        EventStoreDbContext context,
        CancellationToken cancellationToken)
    {
        var summary = await FindAsync(context, envelope, cancellationToken);
        summary.Status = "cancelled";
        summary.TenantOffset = envelope.TenantOffset;
    }

    private static async Task<OrderSummary> FindAsync<TEvent>(
        EventStoreDbContext context,
        EventEnvelope<TEvent> envelope,
        CancellationToken cancellationToken)
        where TEvent : IDomainEvent
    {
        var tenantId = Tenant(envelope);
        var orderId = Guid.Parse(envelope.StreamId);
        return await context.Set<OrderSummary>().SingleAsync(
            value =>
                value.TenantId == tenantId &&
                value.OrderId == orderId,
            cancellationToken);
    }

    private static string Tenant<TEvent>(EventEnvelope<TEvent> envelope)
        where TEvent : IDomainEvent =>
        envelope.TenantId?.Value
        ?? throw new InvalidOperationException("Projected events must have a tenant.");
}

internal sealed class Order(Guid id) : Aggregate<Guid>(id)
{
    private readonly List<OrderItem> items = [];

    public IReadOnlyList<OrderItem> Items => items;
    public string Status { get; private set; } = "new";

    public void Place(string sku, int quantity)
    {
        EnsureValidItem(sku, quantity);
        if (Version != 0)
        {
            throw new InvalidOperationException("An order can only be placed once.");
        }

        Raise(new OrderPlaced(sku, quantity));
    }

    public void AddItem(string sku, int quantity)
    {
        EnsureValidItem(sku, quantity);
        EnsureActive();
        Raise(new OrderItemAdded(sku, quantity));
    }

    public void Cancel(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureActive();
        Raise(new OrderCancelled(reason));
    }

    private void Apply(OrderPlaced @event)
    {
        items.Add(new OrderItem(@event.Sku, @event.Quantity));
        Status = "placed";
    }

    private void Apply(OrderItemAdded @event) => items.Add(new OrderItem(@event.Sku, @event.Quantity));

    private void Apply(OrderCancelled @event) => Status = "cancelled";

    public void Restore(OrderSnapshot snapshot)
    {
        items.Clear();
        items.AddRange(snapshot.Items);
        Status = snapshot.Status;
    }

    private void EnsureActive()
    {
        if (Status != "placed")
        {
            throw new InvalidOperationException("Only a placed order can be changed.");
        }
    }

    private static void EnsureValidItem(string sku, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }
    }
}
