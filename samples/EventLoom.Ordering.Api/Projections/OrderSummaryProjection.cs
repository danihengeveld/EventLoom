using EventLoom.EntityFrameworkCore;
using EventLoom.Ordering.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.Ordering.Api.Projections;

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
    public const string Name = "ordering.order-summary";

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
    {
        var tenantId = Tenant(envelope);
        var orderId = Guid.Parse(envelope.StreamId);
        return await context.Set<OrderSummary>().SingleAsync(
            value => value.TenantId == tenantId && value.OrderId == orderId,
            cancellationToken);
    }

    private static string Tenant<TEvent>(EventEnvelope<TEvent> envelope) =>
        envelope.TenantId?.Value
        ?? throw new InvalidOperationException("Projected events must have a tenant.");
}
