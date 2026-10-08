using EventLoom.Hosting;
using EventLoom.Ordering.Api.Domain;

namespace EventLoom.Ordering.Api.Infrastructure;

internal static partial class OrderingEventLoomBuilderExtensions
{
    public const string PostgreSqlProvider = "PostgreSql";
    public const string MongoDbProvider = "MongoDb";

    public static EventLoomBuilder UseOrderingStorage(
        this EventLoomBuilder eventLoom,
        string provider,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(eventLoom);
        return provider switch
        {
            PostgreSqlProvider => eventLoom.UseOrderingPostgreSql(connectionString),
            MongoDbProvider => eventLoom.UseOrderingMongoDb(connectionString),
            _ => throw new InvalidOperationException(
                $"EventLoom:Provider '{provider}' is not supported. Use '{PostgreSqlProvider}' or '{MongoDbProvider}'.")
        };
    }

    public static EventLoomBuilder AddOrdering(this EventLoomBuilder eventLoom)
    {
        ArgumentNullException.ThrowIfNull(eventLoom);
        return eventLoom
            .UseMultiTenancy<RequestTenantAccessor>()
            .AddAggregate<Order, Guid>(aggregate => aggregate
                .ConstructWith(id => new Order(id))
                .UseStream("order", id => id.ToString("D"))
                .UseSnapshots<OrderSnapshot>(snapshots => snapshots.Every(2)))
            .AddOutboxPublisher<LoggingOutboxPublisher>(options =>
                options.SuccessfulDeliveryRetention = TimeSpan.FromDays(1));
    }
}
