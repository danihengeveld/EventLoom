using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using EventLoom.Hosting;
using EventLoom.MongoDb;
using Testcontainers.MongoDb;

namespace EventLoom.Benchmarks;

public sealed class MongoDbBenchmarkStore : IBenchmarkStore
{
    private MongoDbContainer? container;

    public async Task<Action<EventLoomBuilder>> StartAsync()
    {
        container = new MongoDbBuilder("mongo:8").WithReplicaSet().Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        return builder => builder.UseMongoDb(connectionString, "eventloom_benchmarks");
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring)]
public class MongoDbAppendBenchmarks : AppendBenchmarks
{
    protected override IBenchmarkStore CreateStore() => new MongoDbBenchmarkStore();
}

[MemoryDiagnoser]
public class MongoDbReadBenchmarks : ReadBenchmarks
{
    protected override IBenchmarkStore CreateStore() => new MongoDbBenchmarkStore();
}

[MemoryDiagnoser]
public class MongoDbLoadBenchmarks : LoadBenchmarks
{
    protected override IBenchmarkStore CreateStore() => new MongoDbBenchmarkStore();
}

[MemoryDiagnoser]
public class MongoDbHealthBenchmarks : HealthBenchmarks
{
    protected override IBenchmarkStore CreateStore() => new MongoDbBenchmarkStore();
}
