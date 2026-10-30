using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MongoDB.Bson;
using MongoDB.Driver;
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
    private const int MongoPort = 27017;
    private IContainer? container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
            return;
        }

        // WORKAROUND: the replica set is initiated through the driver instead of the simpler
        // `new MongoDbBuilder("mongo:8").WithReplicaSet().Build()` (Testcontainers.MongoDb).
        // That builder finds mongosh via `docker exec` once and caches the result, including a
        // failed lookup. On GitHub Actions the first lookup fails ("The mongosh or mongo binary
        // could not be found"), so every test fails, while it passes locally.
        // Revert to MongoDbBuilder (and the Testcontainers.MongoDb package reference) once a
        // Testcontainers release no longer caches a failed lookup and CI passes with it; try it
        // on a branch first. Testcontainers.MongoDb is still used by the benchmarks.
        container = new ContainerBuilder("mongo:8")
            .WithPortBinding(MongoPort, true)
            .WithCommand("--replSet", "rs0", "--bind_ip_all")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Waiting for connections"))
            .Build();
        await container.StartAsync();
        ConnectionString = $"mongodb://{container.Hostname}:{container.GetMappedPublicPort(MongoPort)}/?directConnection=true";
        await InitiateReplicaSetAsync();
    }

    private async Task InitiateReplicaSetAsync()
    {
        using var client = new MongoClient(ConnectionString);
        var admin = client.GetDatabase("admin");
        await admin.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["replSetInitiate"] = new BsonDocument
            {
                ["_id"] = "rs0",
                ["members"] = new BsonArray { new BsonDocument { ["_id"] = 0, ["host"] = "127.0.0.1:27017" } },
            },
        });

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1));
            if (hello.GetValue("isWritablePrimary", false).ToBoolean())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The MongoDB replica set did not elect a primary in time.");
            }

            await Task.Delay(200);
        }
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
