using MongoDB.Driver;
using Testcontainers.MongoDb;
using TUnit.Core.Interfaces;

namespace EventLoom.MongoDb.IntegrationTests;

public abstract class MongoDbIntegrationTest
{
    [ClassDataSource<MongoDbTestServer>(Shared = SharedType.PerAssembly)]
    public required MongoDbTestServer Server { get; init; }
}

/// <summary>
/// A single-node MongoDB replica set started through Testcontainers. Set
/// <c>EVENTLOOM_MONGODB_CONNECTION_STRING</c> to run against an existing replica set instead.
/// </summary>
public sealed class MongoDbTestServer : IAsyncInitializer, IAsyncDisposable
{
    private const string ConnectionStringVariable = "EVENTLOOM_MONGODB_CONNECTION_STRING";
    private MongoDbContainer? container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
            return;
        }

        container = new MongoDbBuilder("mongo:8").WithReplicaSet().Build();
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
    }

    public string NewDatabaseName() => $"eventloom_test_{Guid.NewGuid():N}";

    public async Task DropDatabaseAsync(string databaseName)
    {
        using var client = new MongoClient(ConnectionString);
        await client.DropDatabaseAsync(databaseName);
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
