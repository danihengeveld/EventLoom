using EventLoom;
using EventLoom.EntityFrameworkCore;
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
        await Assert.That(results.Count(value => value is EventStoreConcurrencyException)).IsEqualTo(1);
    }

    private static EventStoreDbContext CreateContext(string connectionString, EventStoreOptions options) =>
        new(new DbContextOptionsBuilder<EventStoreDbContext>().UseNpgsql(connectionString).Options, options);

    private static EventStore CreateStore(EventStoreDbContext context, EventRegistry registry) =>
        new(context, new EventSerializer(registry), new UuidV7EventIdGenerator(), TimeProvider.System);

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
