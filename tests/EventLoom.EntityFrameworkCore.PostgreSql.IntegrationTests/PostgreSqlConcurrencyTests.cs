using EventLoom;
using EventLoom.EntityFrameworkCore;
using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlConcurrencyTests
{
    [Test]
    public async Task Concurrent_first_appends_do_not_create_duplicate_stream_versions()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions { UseSchema = true, Schema = "eventloom_test", TablePrefix = "eventloom_" };
        await using var firstContext = CreateContext(container.GetConnectionString(), options);
        await using var secondContext = CreateContext(container.GetConnectionString(), options);
        await firstContext.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Created>();
        var first = CreateStore(firstContext, registry);
        var second = CreateStore(secondContext, registry);
        var streamId = Guid.NewGuid().ToString("D");
        var request = new AppendRequest("tenant-a", streamId, "test", ExpectedVersion.NoStream, [new Created()], new EventMetadata());

        var results = await Task.WhenAll(
            CaptureAsync(() => first.AppendAsync(request)),
            CaptureAsync(() => second.AppendAsync(request)));

        await Assert.That(results.Count(value => value is AppendResult)).IsEqualTo(1);
        await Assert.That(results.Count(value => value is not AppendResult)).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_instances_assign_contiguous_committed_tenant_offsets()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions { UseSchema = true, Schema = "eventloom_offsets", TablePrefix = "eventloom_" };
        await using var setupContext = CreateContext(container.GetConnectionString(), options);
        await setupContext.Database.EnsureCreatedAsync();

        var registry = new EventRegistry().RegisterEvent<Created>();
        var contexts = Enumerable.Range(0, 4)
            .Select(_ => CreateContext(container.GetConnectionString(), options))
            .ToArray();
        try
        {
            var stores = contexts
                .Select(context => CreateStore(
                    context,
                    registry,
                    new PostgreSqlRetryPolicy(
                        new EventStoreWorkerOptions { MaxRetryAttempts = 20 },
                        TimeProvider.System)))
                .ToArray();
            var results = await Task.WhenAll(
                Enumerable.Range(0, stores.Length)
                    .Select((index, _) => stores[index].AppendAsync(new AppendRequest(
                        "tenant-a",
                        Guid.NewGuid().ToString("D"),
                        "test",
                        ExpectedVersion.NoStream,
                        [new Created()],
                        new EventMetadata()))));

            var offsets = results.SelectMany(value => value.Events).Select(value => value.TenantOffset).OrderBy(value => value).ToArray();
            await Assert.That(offsets).IsEquivalentTo(Enumerable.Range(1, stores.Length).Select(value => (long)value).ToArray());
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    private static EventStoreDbContext CreateContext(string connectionString, EventStoreOptions options) =>
        new(new DbContextOptionsBuilder<EventStoreDbContext>().UseNpgsql(connectionString).Options, options);

    private static EventStore CreateStore(
        EventStoreDbContext context,
        EventRegistry registry,
        IEventStoreRetryPolicy? retryPolicy = null) =>
        new(
            context,
            new EventSerializer(registry),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            retryPolicy: retryPolicy);

    private static async Task<object> CaptureAsync(Func<Task<AppendResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [EventType("integration.created")]
    private sealed record Created : IDomainEvent;
}
