using EventLoom.Hosting;
using EventLoom.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Storage.Conformance;

/// <summary>A deterministic clock the conformance tests advance explicitly instead of sleeping.</summary>
public sealed class ConformanceClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan duration) => now += duration;
}

/// <summary>One isolated storage under test: a fresh database or namespace, wired through EventLoom's DI.</summary>
public sealed class StorageEnvironment : IAsyncDisposable
{
    private readonly ServiceProvider services;
    private readonly Func<ValueTask>? cleanup;

    private StorageEnvironment(ServiceProvider services, ConformanceClock clock, Func<ValueTask>? cleanup)
    {
        this.services = services;
        this.cleanup = cleanup;
        Clock = clock;
        Capabilities = services.GetRequiredService<StorageCapabilities>();
    }

    public ConformanceClock Clock { get; }

    public StorageCapabilities Capabilities { get; }

    /// <summary>Creates a new scope, which gets its own storage instances like a request or worker iteration.</summary>
    public AsyncServiceScope CreateScope() => services.CreateAsyncScope();

    /// <summary>Builds an environment from a provider's <c>Use*</c> registration and creates its schema.</summary>
    public static async Task<StorageEnvironment> CreateAsync(
        Action<EventLoomBuilder> useStorage,
        Func<ValueTask>? cleanup = null)
    {
        var clock = new ConformanceClock();
        var collection = new ServiceCollection();
        collection.AddEventLoom(builder =>
        {
            useStorage(builder);
            builder.UseSingleTenancy().UseTimeProvider(clock);
        });
        var provider = collection.BuildServiceProvider(validateScopes: true);
        try
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IStorageSchema>().EnsureCreatedAsync();
            return new StorageEnvironment(provider, clock, cleanup);
        }
        catch
        {
            await provider.DisposeAsync();
            if (cleanup is not null)
            {
                await cleanup();
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await services.DisposeAsync();
        if (cleanup is not null)
        {
            await cleanup();
        }
    }
}
