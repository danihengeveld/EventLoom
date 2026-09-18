using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class OutboxPublisherWorkerTests
{
    [Test]
    public async Task Publisher_retries_with_one_stable_message_id_and_persists_attempt_history()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var recorder = new IdempotentPublisherRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<ItemAdded>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}")
            .AddOutboxPublisher<IdempotentPublisher>(options =>
            {
                options.InstanceId = Guid.NewGuid().ToString("N");
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = TimeSpan.FromSeconds(1);
                options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(100);
                options.MaxRetryAttempts = 1;
                options.SuccessfulDeliveryRetention = TimeSpan.FromDays(1);
            }));
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();

        try
        {
            await EnsureCreatedAsync(provider);
            await worker.StartAsync(CancellationToken.None);
            var eventId = await AppendAsync(provider);
            var delivered = await recorder.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await using var scope = provider.CreateAsyncScope();
            var administration = scope.ServiceProvider.GetRequiredService<OutboxAdministration>();
            var message = await administration.GetAsync("tenant-a", eventId);
            var attempts = await administration.ReadAttemptsAsync("tenant-a", eventId);

            await Assert.That(delivered.MessageId).IsEqualTo(eventId);
            await Assert.That(recorder.VisibleMessageIds).IsEquivalentTo(new[] { eventId });
            await Assert.That(message!.PublishedAt).IsNotNull();
            await Assert.That(message.AttemptCount).IsEqualTo(2);
            await Assert.That(attempts.Count).IsEqualTo(2);
            await Assert.That(attempts[0].Succeeded).IsFalse();
            await Assert.That(attempts[0].ExceptionType).IsEqualTo(typeof(InvalidOperationException).FullName);
            await Assert.That(attempts[1].Succeeded).IsTrue();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(databasePath);
        }
    }

    [Test]
    public async Task Publisher_deletes_successful_message_and_attempts_by_default()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var recorder = new SuccessfulPublisherRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<ItemAdded>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}")
            .AddOutboxPublisher<SuccessfulPublisher>(options =>
            {
                options.InstanceId = Guid.NewGuid().ToString("N");
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = TimeSpan.FromSeconds(1);
                options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(100);
            }));
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();

        try
        {
            await EnsureCreatedAsync(provider);
            await worker.StartAsync(CancellationToken.None);
            var eventId = await AppendAsync(provider);
            await recorder.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForMessageAsync(provider, eventId, expectedToExist: false);

            await using var scope = provider.CreateAsyncScope();
            var administration = scope.ServiceProvider.GetRequiredService<OutboxAdministration>();
            await Assert.That(await administration.ReadAttemptsAsync("tenant-a", eventId)).IsEmpty();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(databasePath);
        }
    }

    [Test]
    public async Task Publisher_removes_retained_successful_message_after_retention_expires()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var recorder = new SuccessfulPublisherRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<ItemAdded>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}")
            .AddOutboxPublisher<SuccessfulPublisher>(options =>
            {
                options.InstanceId = Guid.NewGuid().ToString("N");
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = TimeSpan.FromSeconds(1);
                options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(100);
                options.SuccessfulDeliveryRetention = TimeSpan.FromMilliseconds(250);
            }));
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();

        try
        {
            await EnsureCreatedAsync(provider);
            await worker.StartAsync(CancellationToken.None);
            var eventId = await AppendAsync(provider);
            await recorder.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForMessageAsync(provider, eventId, expectedToExist: true);
            await WaitForMessageAsync(provider, eventId, expectedToExist: false);

            await using var scope = provider.CreateAsyncScope();
            var administration = scope.ServiceProvider.GetRequiredService<OutboxAdministration>();
            await Assert.That(await administration.ReadAttemptsAsync("tenant-a", eventId)).IsEmpty();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(databasePath);
        }
    }

    [Test]
    public async Task Publisher_never_deletes_failed_messages_or_attempts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var recorder = new FailingPublisherRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<ItemAdded>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}")
            .AddOutboxPublisher<FailingPublisher>(options =>
            {
                options.InstanceId = Guid.NewGuid().ToString("N");
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = TimeSpan.FromSeconds(1);
                options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(100);
                options.MaxRetryAttempts = 0;
            }));
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();

        try
        {
            await EnsureCreatedAsync(provider);
            await worker.StartAsync(CancellationToken.None);
            var eventId = await AppendAsync(provider);
            await recorder.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var message = await WaitForAttemptAsync(provider, eventId);
            await worker.StopAsync(CancellationToken.None);

            await using var scope = provider.CreateAsyncScope();
            var administration = scope.ServiceProvider.GetRequiredService<OutboxAdministration>();
            var attempts = await administration.ReadAttemptsAsync("tenant-a", eventId);
            await Assert.That(message.PublishedAt).IsNull();
            await Assert.That(attempts).IsNotEmpty();
            await Assert.That(attempts.All(attempt => !attempt.Succeeded)).IsTrue();
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(databasePath);
        }
    }

    private static async Task EnsureCreatedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.EnsureCreatedAsync();
    }

    private static async Task<Guid> AppendAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(new AppendRequest(
            "tenant-a",
            "order-1",
            "order",
            ExpectedVersion.NoStream,
            [new ItemAdded()],
            new EventMetadata()));
        return result.Events.Single().EventId;
    }

    private static async Task WaitForMessageAsync(
        ServiceProvider provider,
        Guid messageId,
        bool expectedToExist)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            await using var scope = provider.CreateAsyncScope();
            var message = await scope.ServiceProvider.GetRequiredService<OutboxAdministration>()
                .GetAsync("tenant-a", messageId, timeout.Token);
            if ((message is not null) == expectedToExist)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }

        throw new TimeoutException($"Outbox message presence did not become '{expectedToExist}'.");
    }

    private static async Task<OutboxMessage> WaitForAttemptAsync(
        ServiceProvider provider,
        Guid messageId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            await using var scope = provider.CreateAsyncScope();
            var message = await scope.ServiceProvider.GetRequiredService<OutboxAdministration>()
                .GetAsync("tenant-a", messageId, timeout.Token);
            if (message?.AttemptCount > 0)
            {
                return message;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }

        throw new TimeoutException("The outbox publication attempt was not recorded.");
    }

    [EventType("tests.outbox-worker-item-added")]
    private sealed record ItemAdded : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(ItemAdded @event)
        {
        }
    }

    private sealed class IdempotentPublisher(IdempotentPublisherRecorder recorder) : IOutboxPublisher
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken) =>
            recorder.PublishAsync(message);
    }

    private sealed class IdempotentPublisherRecorder
    {
        private readonly HashSet<Guid> visibleMessageIds = [];
        private int calls;

        public TaskCompletionSource<OutboxMessage> Delivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<Guid> VisibleMessageIds
        {
            get
            {
                lock (visibleMessageIds)
                {
                    return visibleMessageIds.ToArray();
                }
            }
        }

        public Task PublishAsync(OutboxMessage message)
        {
            lock (visibleMessageIds)
            {
                visibleMessageIds.Add(message.MessageId);
                calls++;
                if (calls == 1)
                {
                    throw new InvalidOperationException("The transport acknowledgement was lost.");
                }
            }

            Delivered.TrySetResult(message);
            return Task.CompletedTask;
        }
    }

    private sealed class SuccessfulPublisher(SuccessfulPublisherRecorder recorder) : IOutboxPublisher
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            recorder.Delivered.TrySetResult(message);
            return Task.CompletedTask;
        }
    }

    private sealed class SuccessfulPublisherRecorder
    {
        public TaskCompletionSource<OutboxMessage> Delivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FailingPublisher(FailingPublisherRecorder recorder) : IOutboxPublisher
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            recorder.Failed.TrySetResult();
            throw new InvalidOperationException("The transport is unavailable.");
        }
    }

    private sealed class FailingPublisherRecorder
    {
        public TaskCompletionSource Failed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
