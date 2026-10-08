using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;
using EventLoom.Ordering.Api.Domain;
using EventLoom.Ordering.Api.Projections;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.Ordering.Api.Infrastructure;

internal static partial class OrderingEventLoomBuilderExtensions
{
    private static EventLoomBuilder UseOrderingPostgreSql(this EventLoomBuilder eventLoom, string connectionString)
    {
        eventLoom.Services.AddScoped<IOrderSummaryReader, EfOrderSummaryReader>();
        return eventLoom
            .UsePostgreSql(connectionString)
            .ConfigureProjectionModel(modelBuilder =>
                modelBuilder.Entity<OrderSummary>(entity =>
                {
                    entity.ToTable("ordering_order_summaries");
                    entity.HasKey(value => new { value.TenantId, value.OrderId });
                }))
            .AddProjection(OrderSummaryProjection.Name, projection => projection
                .Transactional<OrderSummaryProjection, OrderPlaced>()
                .Transactional<OrderSummaryProjection, OrderItemAdded>()
                .Transactional<OrderSummaryProjection, OrderCancelled>());
    }
}
