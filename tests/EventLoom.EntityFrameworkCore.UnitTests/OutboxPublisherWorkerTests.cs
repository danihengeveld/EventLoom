using EventLoom;
using EventLoom.EntityFrameworkCore;
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
            .ConfigureWorkers(options =>
            {
                options.InstanceId = Guid.NewGuid().ToString("N");
                options.PollInterval = TimeSpan.FromMilliseconds(10);
                options.LeaseDuration = TimeSpan.FromSeconds(1);
                options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(100);
                options.MaxRetryAttempts = 1;
            })
            .UseSqlite($"Data Source={databasePath}")
            .AddOutboxPublisher<IdempotentPublisher>());
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

    [EventType("tests.outbox-worker-item-added")]
    private sealed record ItemAdded : IDomainEvent;

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
}
