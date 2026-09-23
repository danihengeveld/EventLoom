using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace EventLoom.Benchmarks;

public abstract class PostgreSqlBenchmarkBase
{
    private PostgreSqlContainer container = null!;
    private ServiceProvider provider = null!;
    private AsyncServiceScope scope;

    protected const string Tenant = "benchmark";
    protected EventStore Store { get; private set; } = null!;
    protected AggregateRepository<BenchmarkCounter, Guid> Repository { get; private set; } = null!;
    protected IServiceProvider ScopedServices => scope.ServiceProvider;

    protected virtual bool UseSnapshots => false;
    protected virtual bool RegisterProjection => false;

    [GlobalSetup]
    public async Task SetupDatabase()
    {
        container = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await container.StartAsync();

        var services = new ServiceCollection();
        services.AddEventLoom(builder =>
        {
            builder.UseSingleTenancy(Tenant)
                .UsePostgreSql(container.GetConnectionString())
                .AddEvent<CounterIncremented>()
                .AddAggregate<BenchmarkCounter, Guid>(aggregate =>
                {
                    aggregate.ConstructWith(id => new BenchmarkCounter(id))
                        .UseStream("counter", id => id.ToString("D"));
                    if (UseSnapshots)
                    {
                        aggregate.UseSnapshots<CounterSnapshot>(snapshot => snapshot.Every(1));
                    }
                });
            if (RegisterProjection)
            {
                builder.AddProjection("benchmarks.counter", projection =>
                    projection.Asynchronous<NoopProjection, CounterIncremented>());
            }
        });

        provider = services.BuildServiceProvider();
        scope = provider.CreateAsyncScope();
        var scoped = scope.ServiceProvider;
        var context = scoped.GetRequiredService<EventStoreDbContext>();
        if (!await context.Database.EnsureCreatedAsync())
        {
            throw new InvalidOperationException("The benchmark PostgreSQL container was not empty.");
        }

        Store = scoped.GetRequiredService<EventStore>();
        Repository = scoped.GetRequiredService<AggregateRepository<BenchmarkCounter, Guid>>();
        await SeedAsync();
    }

    protected virtual Task SeedAsync() => Task.CompletedTask;

    protected async Task SeedStreamAsync(Guid id, int count)
    {
        await Store.AppendAsync(new AppendRequest(
            Tenant,
            id.ToString("D"),
            "counter",
            ExpectedVersion.NoStream,
            Enumerable.Range(0, count)
                .Select(_ => (object)new CounterIncremented(1, "benchmark"))
                .ToArray(),
            new EventMetadata()));
    }

    [GlobalCleanup]
    public async Task CleanupDatabase()
    {
        if (provider is not null)
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }

        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

public sealed class NoopProjection : IProjectionHandler<CounterIncremented>
{
    public Task HandleAsync(EventEnvelope<CounterIncremented> envelope, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring)]
public class PostgreSqlAppendBenchmarks : PostgreSqlBenchmarkBase
{
    private string streamId = null!;
    private object[] events = null!;

    [Params(1, 10)] public int BatchSize { get; set; }

    protected override Task SeedAsync()
    {
        events = Enumerable.Range(0, BatchSize)
            .Select(_ => (object)new CounterIncremented(1, "benchmark"))
            .ToArray();
        return Task.CompletedTask;
    }

    [IterationSetup]
    public void PrepareExistingStream()
    {
        var id = Guid.NewGuid();
        streamId = id.ToString("D");
        SeedStreamAsync(id, 1).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task<AppendResult> AppendToExistingStream() =>
        Store.AppendAsync(new AppendRequest(
            Tenant, streamId, "counter", ExpectedVersion.StreamExists, events, new EventMetadata()));
}

[MemoryDiagnoser]
public class PostgreSqlReadBenchmarks : PostgreSqlBenchmarkBase
{
    private string streamId = null!;

    [Params(10, 100, 1000)] public int EventCount { get; set; }

    protected override async Task SeedAsync()
    {
        var id = Guid.NewGuid();
        streamId = id.ToString("D");
        await SeedStreamAsync(id, EventCount);
    }

    [Benchmark]
    public Task<IReadOnlyList<EventEnvelope>> ReadStream() =>
        Store.ReadStreamAsync(Tenant, streamId);
}

[MemoryDiagnoser]
public class PostgreSqlLoadBenchmarks : PostgreSqlBenchmarkBase
{
    private Guid id;

    [Params(10, 100, 1000)] public int EventCount { get; set; }

    [Params(false, true)] public bool Snapshots { get; set; }

    protected override bool UseSnapshots => Snapshots;

    protected override async Task SeedAsync()
    {
        id = Guid.NewGuid();
        if (!Snapshots)
        {
            await SeedStreamAsync(id, EventCount);
            return;
        }

        var tailCount = Math.Min(10, EventCount - 1);
        var aggregate = new BenchmarkCounter(id);
        for (var i = 0; i < EventCount - tailCount; i++)
        {
            aggregate.Increment("benchmark");
        }

        await Repository.SaveAsync(aggregate);
        await Store.AppendAsync(new AppendRequest(
            Tenant,
            id.ToString("D"),
            "counter",
            ExpectedVersion.Exact(EventCount - tailCount),
            Enumerable.Range(0, tailCount)
                .Select(_ => (object)new CounterIncremented(1, "benchmark"))
                .ToArray(),
            new EventMetadata()));
    }

    [Benchmark]
    public Task<BenchmarkCounter> LoadAggregate() => Repository.LoadAsync(id);
}

[MemoryDiagnoser]
public class PostgreSqlHealthBenchmarks : PostgreSqlBenchmarkBase
{
    private EventLoomOperationalDiagnostics diagnostics = null!;

    [Params(100, 1000)] public int EventCount { get; set; }

    protected override bool RegisterProjection => true;

    protected override async Task SeedAsync()
    {
        await SeedStreamAsync(Guid.NewGuid(), EventCount);
        diagnostics = ScopedServices.GetRequiredService<EventLoomOperationalDiagnostics>();
    }

    [Benchmark]
    public Task<EventLoomOperationalSummary> ReadHealthSummary() => diagnostics.GetAsync();
}
