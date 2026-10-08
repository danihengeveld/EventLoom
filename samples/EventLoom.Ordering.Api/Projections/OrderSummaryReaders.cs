using EventLoom.EntityFrameworkCore;
using EventLoom.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace EventLoom.Ordering.Api.Projections;

internal interface IOrderSummaryReader
{
    Task<OrderSummary?> FindAsync(string tenantId, Guid orderId, CancellationToken cancellationToken);
}

internal sealed class EfOrderSummaryReader(EventStoreDbContext context) : IOrderSummaryReader
{
    public Task<OrderSummary?> FindAsync(string tenantId, Guid orderId, CancellationToken cancellationToken) =>
        context.Set<OrderSummary>().AsNoTracking().SingleOrDefaultAsync(
            value => value.TenantId == tenantId && value.OrderId == orderId,
            cancellationToken);
}

internal sealed class MongoOrderSummaryReader(MongoSessionAccessor mongo) : IOrderSummaryReader
{
    public async Task<OrderSummary?> FindAsync(string tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        var document = await mongo.Database
            .GetCollection<OrderSummaryDocument>(MongoOrderSummaryProjection.CollectionName)
            .Find(value => value.Id == OrderSummaryDocument.KeyFor(tenantId, orderId.ToString("D")))
            .SingleOrDefaultAsync(cancellationToken);
        return document?.ToSummary();
    }
}
