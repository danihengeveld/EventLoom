using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class OperationalLoggingTests
{
    [Test]
    public async Task PostgreSql_retry_logs_classification_without_exception_details()
    {
        var logger = new RecordingLogger<PostgreSqlRetryPolicy>();
        var policy = new PostgreSqlRetryPolicy(
            new EventStoreWorkerOptions { MaxRetryAttempts = 1 }, TimeProvider.System, logger);
        var attempts = 0;

        var result = await policy.ExecuteAsync<int>(_ =>
        {
            if (attempts++ == 0)
            {
                throw new NpgsqlException("private connection details");
            }

            return Task.FromResult(42);
        });

        var retry = await logger.WaitForAsync(2000);
        await Assert.That(result).IsEqualTo(42);
        await Assert.That(attempts).IsEqualTo(2);
        await Assert.That(retry.Level).IsEqualTo(LogLevel.Debug);
        await Assert.That(retry.Properties["Classification"]).IsEqualTo(PostgreSqlExceptionClassification.Transient);
        await Assert.That(retry.Properties["Attempt"]).IsEqualTo(1);
        await Assert.That(retry.Message.Contains("private connection details", StringComparison.Ordinal)).IsFalse();
        await Assert.That(retry.Exception).IsNull();
    }

    [Test]
    public async Task Unexpected_append_failure_logs_type_without_application_data()
    {
        var logger = new RecordingLogger<EventStore>();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite("Data Source=:memory:").Options,
            new EventStoreOptions());
        var store = new EventStore(
            context,
            new EventSerializer(new EventRegistry().RegisterEvent<Added>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            null, null, new FailingRetryPolicy(), null, logger);
        var request = new AppendRequest(
            "tenant-private", "stream-private", "test",
            ExpectedVersion.NoStream, [new Added()], new EventMetadata());

        await Assert.That(async () => await store.AppendAsync(request)).Throws<InvalidOperationException>();

        var failure = await logger.WaitForAsync(1001);
        await Assert.That(failure.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(failure.Properties["AggregateType"]).IsEqualTo("test");
        await Assert.That(failure.Properties["ExceptionType"]).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(failure.Properties.ContainsKey("TenantId")).IsFalse();
        await Assert.That(failure.Properties.ContainsKey("StreamId")).IsFalse();
        await Assert.That(failure.Message.Contains("private", StringComparison.Ordinal)).IsFalse();
        await Assert.That(failure.Exception).IsNull();
    }

    [Test]
    public async Task Expected_version_conflict_logs_at_debug_not_error()
    {
        var logger = new RecordingLogger<EventStore>();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions());
        await context.Database.EnsureCreatedAsync();
        var store = new EventStore(
            context,
            new EventSerializer(new EventRegistry().RegisterEvent<Added>()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            null, null, null, null, logger);
        var request = new AppendRequest(
            "tenant-private", "stream-private", "test",
            ExpectedVersion.NoStream, [new Added()], new EventMetadata());

        await store.AppendAsync(request);
        await Assert.That(async () => await store.AppendAsync(request)).Throws<WrongExpectedVersionException>();

        var rejected = await logger.WaitForAsync(1000);
        await Assert.That(rejected.Level).IsEqualTo(LogLevel.Debug);
        await Assert.That(rejected.Properties["ExceptionType"]).IsEqualTo(typeof(WrongExpectedVersionException).FullName);
        await Assert.That(logger.Entries.Any(entry => entry.EventId == 1001)).IsFalse();
        await Assert.That(rejected.Message.Contains("private", StringComparison.Ordinal)).IsFalse();
        await Assert.That(rejected.Exception).IsNull();
    }

    [Test]
    public async Task AddEventLoom_emits_logs_to_the_host_provider_without_an_opt_in()
    {
        var logs = new RecordingLogger<object>();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(new CaptureProvider(logs)));
        services.AddEventLoom(eventLoom => eventLoom
            .AddEvent<Added>()
            .UseSingleTenancy("tenant-private")
            .UseSqlite("Data Source=:memory:"));
        services.AddScoped<IEventStoreRetryPolicy>(_ => new FailingRetryPolicy());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var request = new AppendRequest(
            "tenant-private", "stream-private", "test",
            ExpectedVersion.NoStream, [new Added()], new EventMetadata());
        await Assert.That(async () => await scope.ServiceProvider.GetRequiredService<EventStore>()
            .AppendAsync(request)).Throws<InvalidOperationException>();

        var failure = await logs.WaitForAsync(1001);
        await Assert.That(failure.Level).IsEqualTo(LogLevel.Error);
        await Assert.That(failure.Message.Contains("private", StringComparison.Ordinal)).IsFalse();
        await Assert.That(failure.Exception).IsNull();
    }

    [Test]
    public async Task Host_minimum_level_can_suppress_EventLoom_logs()
    {
        var logs = new RecordingLogger<object>();
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddProvider(new CaptureProvider(logs));
            builder.AddFilter("EventLoom", LogLevel.Critical);
        });
        services.AddEventLoom(eventLoom => eventLoom
            .AddEvent<Added>()
            .UseSingleTenancy("tenant-private")
            .UseSqlite("Data Source=:memory:"));
        services.AddScoped<IEventStoreRetryPolicy>(_ => new FailingRetryPolicy());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var request = new AppendRequest(
            "tenant-private", "stream-private", "test",
            ExpectedVersion.NoStream, [new Added()], new EventMetadata());
        await Assert.That(async () => await scope.ServiceProvider.GetRequiredService<EventStore>()
            .AppendAsync(request)).Throws<InvalidOperationException>();

        await Assert.That(logs.Entries).IsEmpty();
    }

    [EventType("tests.operational-log-added")]
    private sealed record Added : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(Added @event)
        {
        }
    }

    private sealed class FailingRetryPolicy : IEventStoreRetryPolicy
    {
        public Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new InvalidOperationException("tenant-private stream-private"));
    }

    private sealed class CaptureProvider(RecordingLogger<object> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logs;

        public void Dispose()
        {
        }
    }
}
