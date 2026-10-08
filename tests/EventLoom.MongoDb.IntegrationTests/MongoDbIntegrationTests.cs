using EventLoom.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EventLoom.MongoDb.IntegrationTests;

public sealed class MongoDbIntegrationTests : MongoDbIntegrationTest
{
    private const string Tenant = "tenant-a";

    [Test]
    public async Task Transactional_projection_commits_the_read_model_with_its_checkpoint()
    {
        var databaseName = Server.NewDatabaseName();
        await using var services = CreateServices(databaseName, registerProjection: true);
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStorageSchema>().EnsureCreatedAsync();
            var repository = scope.ServiceProvider.GetRequiredService<AggregateRepository<Cart, Guid>>();
            var cart = new Cart(Guid.NewGuid());
            cart.Add("coffee");
            cart.Add("tea");
            await repository.SaveAsync(cart);
        }

        var hosted = services.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            var database = services.GetRequiredService<IMongoClient>().GetDatabase(databaseName);
            var summaries = database.GetCollection<BsonDocument>(CartSummaryProjection.CollectionName);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            long count;
            do
            {
                count = await summaries.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
                if (count == 2)
                {
                    break;
                }

                await Task.Delay(50);
            } while (DateTimeOffset.UtcNow < deadline);

            await Assert.That(count).IsEqualTo(2);
            await using var scope = services.CreateAsyncScope();
            var checkpoint = await scope.ServiceProvider.GetRequiredService<ProjectionAdministration>()
                .GetCheckpointAsync(Tenant, new ProjectionKey(CartSummaryProjection.Name, 1));
            await Assert.That(checkpoint!.TenantOffset).IsEqualTo(2);
        }
        finally
        {
            foreach (var service in hosted)
            {
                await service.StopAsync(CancellationToken.None);
            }

            await Server.DropDatabaseAsync(databaseName);
        }
    }

    [Test]
    public async Task Unit_of_work_commits_application_documents_and_events_atomically()
    {
        var databaseName = Server.NewDatabaseName();
        await using var services = CreateServices(databaseName, registerProjection: false);
        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStorageSchema>().EnsureCreatedAsync();
            var store = scope.ServiceProvider.GetRequiredService<EventStore>();
            var records = services.GetRequiredService<IMongoClient>().GetDatabase(databaseName)
                .GetCollection<BsonDocument>("application_records");

            await using (var unitOfWork = await store.BeginUnitOfWorkAsync())
            {
                await records.InsertOneAsync(unitOfWork.Session, new BsonDocument("_id", "committed"));
                await unitOfWork.AppendAsync(Request("cart-1"));
                await unitOfWork.CommitAsync();
            }

            await using (var unitOfWork = await store.BeginUnitOfWorkAsync())
            {
                await records.InsertOneAsync(unitOfWork.Session, new BsonDocument("_id", "rolled-back"));
                await unitOfWork.AppendAsync(Request("cart-2"));
                await unitOfWork.RollbackAsync();
            }

            await Assert.That((await store.ReadStreamAsync(Tenant, "cart-1")).Count).IsEqualTo(1);
            await Assert.That((await store.ReadStreamAsync(Tenant, "cart-2")).Count).IsEqualTo(0);
            await Assert.That(await records.CountDocumentsAsync(new BsonDocument("_id", "committed"))).IsEqualTo(1);
            await Assert.That(await records.CountDocumentsAsync(new BsonDocument("_id", "rolled-back"))).IsEqualTo(0);
        }
        finally
        {
            await Server.DropDatabaseAsync(databaseName);
        }
    }

    [Test]
    public async Task Validation_reports_missing_schema_until_it_is_created()
    {
        var databaseName = Server.NewDatabaseName();
        await using var services = CreateServices(databaseName, registerProjection: false);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var schema = scope.ServiceProvider.GetRequiredService<IStorageSchema>();

            var before = await schema.ValidateAsync();
            await schema.EnsureCreatedAsync();
            var after = await schema.ValidateAsync();

            await Assert.That(before.CanConnect).IsTrue();
            await Assert.That(before.IsCompatible).IsFalse();
            await Assert.That(after.IsCompatible).IsTrue();
        }
        finally
        {
            await Server.DropDatabaseAsync(databaseName);
        }
    }

    [Test]
    public async Task Application_documents_can_store_guid_values()
    {
        var databaseName = Server.NewDatabaseName();
        await using var services = CreateServices(databaseName, registerProjection: false);
        try
        {
            var database = services.GetRequiredService<IMongoClient>().GetDatabase(databaseName);
            var collection = database.GetCollection<GuidDocument>("guid_documents");
            var document = new GuidDocument { Id = Guid.NewGuid(), OrderId = Guid.NewGuid() };

            await collection.InsertOneAsync(document);
            var loaded = await collection.Find(candidate => candidate.Id == document.Id).SingleAsync();

            await Assert.That(loaded.OrderId).IsEqualTo(document.OrderId);
        }
        finally
        {
            await Server.DropDatabaseAsync(databaseName);
        }
    }

    private ServiceProvider CreateServices(string databaseName, bool registerProjection)
    {
        var collection = new ServiceCollection();
        collection.AddEventLoom(builder =>
        {
            builder.UseMongoDb(Server.ConnectionString, databaseName)
                .UseSingleTenancy(Tenant)
                .ConfigureWorkers(options => options.PollInterval = TimeSpan.FromMilliseconds(50))
                .AddAggregate<Cart, Guid>(aggregate => aggregate
                    .ConstructWith(id => new Cart(id))
                    .UseStream("cart", id => id.ToString("D")));
            if (registerProjection)
            {
                builder.AddProjection(CartSummaryProjection.Name, projection => projection
                    .Transactional<CartSummaryProjection, ItemAdded>());
            }
        });
        return collection.BuildServiceProvider(validateScopes: true);
    }

    private static AppendRequest Request(string streamId) =>
        new(Tenant, streamId, "cart", ExpectedVersion.NoStream, [new ItemAdded("coffee")], new EventMetadata());
}

public sealed class GuidDocument
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }
}

public sealed record ItemAdded(string Sku) : IDomainEvent<ItemAdded, Cart>
{
    public static string EventType => "tests.cart-item-added";
}

public sealed class Cart(Guid id) : Aggregate<Cart, Guid>(id), IApply<ItemAdded>
{
    public void Add(string sku) => Raise(new ItemAdded(sku));

    void IApply<ItemAdded>.Apply(ItemAdded @event)
    {
    }
}

public sealed class CartSummaryProjection : IMongoProjectionHandler<ItemAdded>
{
    public const string Name = "tests.cart-summary";
    public const string CollectionName = "cart_summaries";

    public Task HandleAsync(
        EventEnvelope<ItemAdded> envelope,
        MongoProjectionTransaction transaction,
        CancellationToken cancellationToken) =>
        transaction.Database.GetCollection<BsonDocument>(CollectionName).InsertOneAsync(
            transaction.Session,
            new BsonDocument { ["_id"] = envelope.EventId.ToString("D"), ["sku"] = envelope.Event.Sku },
            cancellationToken: cancellationToken);
}
