using EventLoom.Hosting;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlConcurrencyTests : PostgreSqlIntegrationTest
{
    [Test]
    public async Task Concurrent_first_appends_do_not_create_duplicate_stream_versions()
    {
        await using var database = await Server.CreateDatabaseAsync();
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();

        var registry = new EventRegistry().RegisterEvent<Created>();
        var first = CreateStore(firstContext, registry);
        var second = CreateStore(secondContext, registry);
        var streamId = Guid.NewGuid().ToString("D");
        var request = new AppendRequest("tenant-a", streamId, "test", ExpectedVersion.NoStream, [new Created()],
            new EventMetadata());

        var results = await Task.WhenAll(
            CaptureAsync(() => first.AppendAsync(request)),
            CaptureAsync(() => second.AppendAsync(request)));

        await Assert.That(results.Count(value => value is AppendResult)).IsEqualTo(1);
        await Assert.That(results.Count(value => value is not AppendResult)).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_first_appends_with_the_same_append_id_replay_one_result()
    {
        var options = new EventStoreOptions
            { UseSchema = true, Schema = "eventloom_idempotency", TablePrefix = "eventloom_" };
        await using var database = await Server.CreateDatabaseAsync(options);
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();

        var registry = new EventRegistry().RegisterEvent<Created>();
        var retryOptions = new EventStoreWorkerOptions { MaxRetryAttempts = 20 };
        var first = CreateStore(firstContext, registry, new PostgreSqlRetryPolicy(retryOptions, TimeProvider.System));
        var second = CreateStore(secondContext, registry, new PostgreSqlRetryPolicy(retryOptions, TimeProvider.System));
        var streamId = Guid.NewGuid().ToString("D");
        var request = new AppendRequest(
            "tenant-a",
            streamId,
            "test",
            ExpectedVersion.NoStream,
            [new Created()],
            new EventMetadata(),
            AppendId: "first-write-command");

        var results = await Task.WhenAll(first.AppendAsync(request), second.AppendAsync(request));
        var history = await first.ReadStreamAsync("tenant-a", streamId);

        await Assert.That(results.Count(value => value.WasIdempotentReplay)).IsEqualTo(1);
        await Assert.That(results.Count(value => !value.WasIdempotentReplay)).IsEqualTo(1);
        await Assert.That(history.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_instances_assign_contiguous_committed_tenant_offsets()
    {
        var options = new EventStoreOptions
            { UseSchema = true, Schema = "eventloom_offsets", TablePrefix = "eventloom_" };
        await using var database = await Server.CreateDatabaseAsync(options);

        var registry = new EventRegistry().RegisterEvent<Created>();
        var contexts = Enumerable.Range(0, 4)
            .Select(_ => database.CreateContext())
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

            var offsets = results.SelectMany(value => value.Events).Select(value => value.TenantOffset)
                .OrderBy(value => value).ToArray();
            await Assert.That(offsets)
                .IsEquivalentTo(Enumerable.Range(1, stores.Length).Select(value => (long)value).ToArray());
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

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
    private sealed record Created : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(Created @event)
        {
        }
    }
}
