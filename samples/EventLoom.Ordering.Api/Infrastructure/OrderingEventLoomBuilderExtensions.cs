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
        var snapshots = new AggregateSnapshotAdapter<Order, OrderSnapshot>(
            order => new OrderSnapshot(order.Status, order.Items.ToArray()),
            (order, snapshot) => order.Restore(snapshot));

        return eventLoom
            .UseMultiTenancy<RequestTenantAccessor>()
            .AddEvent<OrderPlaced>()
            .AddEvent<OrderItemAdded>()
            .AddEvent<OrderCancelled>()
            .ConfigureProjectionModel(ConfigureReadModels)
            .AddAggregate<Order, Guid>(aggregate => aggregate
                .ConstructWith(id => new Order(id))
                .UseStream("order", id => id.ToString("D"))
                .UseSnapshots(snapshots, new EveryNEventsSnapshotPolicy(2)))
            .AddOutboxPublisher<LoggingOutboxPublisher>(options =>
                options.SuccessfulDeliveryRetention = TimeSpan.FromDays(1))
            .AddEfProjection<OrderSummaryProjection, OrderPlaced>(OrderSummaryProjection.Name)
            .AddEfProjection<OrderSummaryProjection, OrderItemAdded>(OrderSummaryProjection.Name)
            .AddEfProjection<OrderSummaryProjection, OrderCancelled>(OrderSummaryProjection.Name);
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
