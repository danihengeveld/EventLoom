using EventLoom.Hosting;
using EventLoom.Ordering.Api.Domain;
using EventLoom.Ordering.Api.Projections;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.Ordering.Api.Infrastructure;

internal static class OrderingEventLoomBuilderExtensions
{
    public static EventLoomBuilder AddOrdering(this EventLoomBuilder eventLoom)
    {
        ArgumentNullException.ThrowIfNull(eventLoom);
        return eventLoom
            .UseMultiTenancy<RequestTenantAccessor>()
            .AddEvent<OrderPlaced>()
            .AddEvent<OrderItemAdded>()
            .AddEvent<OrderCancelled>()
            .ConfigureProjectionModel(ConfigureReadModels)
            .AddAggregate<Order, Guid>(aggregate => aggregate
                .ConstructWith(id => new Order(id))
                .UseStream("order", id => id.ToString("D"))
                .UseSnapshots<OrderSnapshot>(snapshots => snapshots.Every(2)))
            .AddOutboxPublisher<LoggingOutboxPublisher>(options =>
                options.SuccessfulDeliveryRetention = TimeSpan.FromDays(1))
            .AddProjection(OrderSummaryProjection.Name, projection => projection
                .Transactional<OrderSummaryProjection, OrderPlaced>()
                .Transactional<OrderSummaryProjection, OrderItemAdded>()
                .Transactional<OrderSummaryProjection, OrderCancelled>());
    }

    private static void ConfigureReadModels(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderSummary>(entity =>
        {
            entity.ToTable("ordering_order_summaries");
            entity.HasKey(value => new { value.TenantId, value.OrderId });
        });
    }
}
