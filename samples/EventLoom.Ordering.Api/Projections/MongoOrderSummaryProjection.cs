using EventLoom.MongoDb;
using EventLoom.Ordering.Api.Domain;
using MongoDB.Driver;

namespace EventLoom.Ordering.Api.Projections;

internal sealed class OrderSummaryDocument
{
    public required string Id { get; set; }
    public required string TenantId { get; set; }
    public required string OrderId { get; set; }
    public required string Status { get; set; }
    public int ItemCount { get; set; }
    public int TotalQuantity { get; set; }
    public long TenantOffset { get; set; }

    public static string KeyFor(string tenantId, string orderId) => $"{tenantId}:{orderId}";

    public OrderSummary ToSummary() => new()
    {
        TenantId = TenantId,
        OrderId = Guid.Parse(OrderId),
        Status = Status,
        ItemCount = ItemCount,
        TotalQuantity = TotalQuantity,
        TenantOffset = TenantOffset
    };
}

internal sealed class MongoOrderSummaryProjection :
    IMongoProjectionHandler<OrderPlaced>,
    IMongoProjectionHandler<OrderItemAdded>,
    IMongoProjectionHandler<OrderCancelled>
{
    public const string CollectionName = "ordering_order_summaries";

    public async Task HandleAsync(
        EventEnvelope<OrderPlaced> envelope,
        MongoProjectionTransaction transaction,
        CancellationToken cancellationToken)
    {
        var tenantId = Tenant(envelope);
        await Summaries(transaction).ReplaceOneAsync(
            transaction.Session,
            Filter(tenantId, envelope.StreamId),
            new OrderSummaryDocument
            {
                Id = OrderSummaryDocument.KeyFor(tenantId, envelope.StreamId),
                TenantId = tenantId,
                OrderId = envelope.StreamId,
                Status = "active",
                ItemCount = 1,
                TotalQuantity = envelope.Event.Quantity,
                TenantOffset = envelope.TenantOffset
            },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public Task HandleAsync(
        EventEnvelope<OrderItemAdded> envelope,
        MongoProjectionTransaction transaction,
        CancellationToken cancellationToken) =>
        Summaries(transaction).UpdateOneAsync(
            transaction.Session,
            Filter(Tenant(envelope), envelope.StreamId),
            Builders<OrderSummaryDocument>.Update
                .Inc(value => value.ItemCount, 1)
                .Inc(value => value.TotalQuantity, envelope.Event.Quantity)
                .Set(value => value.TenantOffset, envelope.TenantOffset),
            cancellationToken: cancellationToken);

    public Task HandleAsync(
        EventEnvelope<OrderCancelled> envelope,
        MongoProjectionTransaction transaction,
        CancellationToken cancellationToken) =>
        Summaries(transaction).UpdateOneAsync(
            transaction.Session,
            Filter(Tenant(envelope), envelope.StreamId),
            Builders<OrderSummaryDocument>.Update
                .Set(value => value.Status, "cancelled")
                .Set(value => value.TenantOffset, envelope.TenantOffset),
            cancellationToken: cancellationToken);

    private static IMongoCollection<OrderSummaryDocument> Summaries(MongoProjectionTransaction transaction) =>
        transaction.Database.GetCollection<OrderSummaryDocument>(CollectionName);

    private static FilterDefinition<OrderSummaryDocument> Filter(string tenantId, string orderId) =>
        Builders<OrderSummaryDocument>.Filter.Eq(value => value.Id, OrderSummaryDocument.KeyFor(tenantId, orderId));

    private static string Tenant<TEvent>(EventEnvelope<TEvent> envelope) =>
        envelope.TenantId?.Value
        ?? throw new InvalidOperationException("Projected events must have a tenant.");
}
