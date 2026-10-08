using EventLoom.Storage.Conformance;

namespace EventLoom.MongoDb.IntegrationTests;

[InheritsTests]
public sealed class MongoDbStorageConformanceTests : StorageConformanceTests
{
    [ClassDataSource<MongoDbTestServer>(Shared = SharedType.PerAssembly)]
    public required MongoDbTestServer Server { get; init; }

    protected override async Task<StorageEnvironment> CreateEnvironmentAsync()
    {
        var databaseName = Server.NewDatabaseName();
        return await StorageEnvironment.CreateAsync(
            builder => builder.UseMongoDb(Server.ConnectionString, databaseName),
            async () => await Server.DropDatabaseAsync(databaseName));
    }
}
