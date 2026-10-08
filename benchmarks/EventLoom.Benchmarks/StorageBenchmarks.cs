using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using EventLoom.Hosting;
using EventLoom.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Benchmarks;

/// <summary>A disposable database a benchmark runs against, started once per benchmark class.</summary>
public interface IBenchmarkStore : IAsyncDisposable
{
    /// <summary>Starts the database and returns the provider registration for it.</summary>
    Task<Action<EventLoomBuilder>> StartAsync();
}

public abstract class StorageBenchmarkBase
{
    private IBenchmarkStore store = null!;
    private ServiceProvider provider = null!;
    private AsyncServiceScope scope;

    protected const string Tenant = "benchmark";
    protected EventStore Store { get; private set; } = null!;
    protected AggregateRepository<BenchmarkCounter, Guid> Repository { get; private set; } = null!;
    protected IServiceProvider ScopedServices => scope.ServiceProvider;

    protected virtual bool UseSnapshots => false;
    protected virtual bool RegisterProjection => false;

    protected abstract IBenchmarkStore CreateStore();

    [GlobalSetup]
    public async Task SetupDatabase()
    {
        store = CreateStore();
        var useStorage = await store.StartAsync();

        var services = new ServiceCollection();
        services.AddEventLoom(builder =>
        {
            useStorage(builder);
            builder.UseSingleTenancy(Tenant)
                .AddAggregateEvents<BenchmarkCounter>()
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
        await scoped.GetRequiredService<IStorageSchema>().EnsureCreatedAsync();

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

        if (store is not null)
        {
            await store.DisposeAsync();
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
public abstract class AppendBenchmarks : StorageBenchmarkBase
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
public abstract class ReadBenchmarks : StorageBenchmarkBase
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
public abstract class LoadBenchmarks : StorageBenchmarkBase
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
public abstract class HealthBenchmarks : StorageBenchmarkBase
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
