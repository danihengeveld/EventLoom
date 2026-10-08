using EventLoom.Hosting;
using EventLoom.MongoDb;
using EventLoom.Ordering.Api.Domain;
using EventLoom.Ordering.Api.Projections;
using MongoDB.Driver;

namespace EventLoom.Ordering.Api.Infrastructure;

internal static partial class OrderingEventLoomBuilderExtensions
{
    private static EventLoomBuilder UseOrderingMongoDb(this EventLoomBuilder eventLoom, string connectionString)
    {
        eventLoom.Services.AddScoped<IOrderSummaryReader, MongoOrderSummaryReader>();
        var databaseName = MongoUrl.Create(connectionString).DatabaseName ?? "eventloom";
        return eventLoom
            .UseMongoDb(connectionString, databaseName)
            .AddProjection(OrderSummaryProjection.Name, projection => projection
                .Transactional<MongoOrderSummaryProjection, OrderPlaced>()
                .Transactional<MongoOrderSummaryProjection, OrderItemAdded>()
                .Transactional<MongoOrderSummaryProjection, OrderCancelled>());
    }
}
