using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;
using Testcontainers.PostgreSql;

namespace EventLoom.Benchmarks;

public sealed class PostgreSqlBenchmarkStore : IBenchmarkStore
{
    private PostgreSqlContainer? container;

    public async Task<Action<EventLoomBuilder>> StartAsync()
    {
        container = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await container.StartAsync();
        var connectionString = container.GetConnectionString();
        return builder => builder.UsePostgreSql(connectionString);
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
public class PostgreSqlAppendBenchmarks : AppendBenchmarks
{
    protected override IBenchmarkStore CreateStore() => new PostgreSqlBenchmarkStore();
}

[MemoryDiagnoser]
public class PostgreSqlReadBenchmarks : ReadBenchmarks
{
    protected override IBenchmarkStore CreateStore() => new PostgreSqlBenchmarkStore();
}

[MemoryDiagnoser]
public class PostgreSqlLoadBenchmarks : LoadBenchmarks
{
    protected override IBenchmarkStore CreateStore() => new PostgreSqlBenchmarkStore();
}

[MemoryDiagnoser]
public class PostgreSqlHealthBenchmarks : HealthBenchmarks
{
    protected override IBenchmarkStore CreateStore() => new PostgreSqlBenchmarkStore();
}
