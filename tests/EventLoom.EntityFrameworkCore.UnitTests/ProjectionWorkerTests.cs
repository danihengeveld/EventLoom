using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SQLitePCL;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class ProjectionWorkerTests
{
    [Test]
    public async Task Hosted_worker_dispatches_typed_envelopes_and_advances_checkpoints()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var recorder = new ProjectionRecorder();
        var services = CreateServices(databasePath, recorder);
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();

        try
        {
            await EnsureCreatedAsync(provider);
            await worker.StartAsync(CancellationToken.None);
            await AppendAsync(provider);

            var envelope = await recorder.Handled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var checkpoint = await WaitForCheckpointAsync(
                provider,
                new ProjectionKey("tests.recording", 1),
                ProjectionStatus.Running);

            await Assert.That(envelope.Metadata.CorrelationId).IsEqualTo("correlation-1");
            await Assert.That(envelope.TenantOffset).IsEqualTo(1);
            await Assert.That(checkpoint!.TenantOffset).IsEqualTo(1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(databasePath);
        }
    }

    [Test]
    public async Task Poison_event_pauses_only_its_projection_after_bounded_retries()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var recorder = new ProjectionRecorder();
        var failures = new ProjectionFailureRecorder();
        var services = CreateServices(databasePath, recorder, failures);
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();

        try
        {
            await EnsureCreatedAsync(provider);
            await worker.StartAsync(CancellationToken.None);
            await AppendAsync(provider);

            await Task.WhenAll(
                recorder.Handled.Task,
                failures.Retried.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await using var scope = provider.CreateAsyncScope();
            var administration = scope.ServiceProvider.GetRequiredService<ProjectionAdministration>();
            var failedCheckpoint = await WaitForCheckpointAsync(
                provider,
                new ProjectionKey("tests.failing", 1),
                ProjectionStatus.Paused);
            var successfulCheckpoint = await WaitForCheckpointAsync(
                provider,
                new ProjectionKey("tests.recording", 1),
                ProjectionStatus.Running);
            var persistedFailures =
                await administration.ReadFailuresAsync("tenant-a", new ProjectionKey("tests.failing", 1));

            await Assert.That(failures.Attempts).IsEqualTo(2);
            await Assert.That(failedCheckpoint!.Status).IsEqualTo(ProjectionStatus.Paused);
            await Assert.That(successfulCheckpoint!.Status).IsEqualTo(ProjectionStatus.Running);
            await Assert.That(successfulCheckpoint.TenantOffset).IsEqualTo(1);
            await Assert.That(persistedFailures.Single().AttemptCount).IsEqualTo(2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            File.Delete(databasePath);
        }
    }

    [Test]
    public async Task Inline_ef_projection_commits_with_its_event_append()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        Batteries_V2.Init();
        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<ItemAdded>()
            .UseSingleTenancy("tenant-a")
            .ConfigureProjectionModel(modelBuilder =>
            {
                modelBuilder.Entity<InlineReadModel>(entity =>
                {
                    entity.ToTable("inline_read_models");
                    entity.HasKey(value => value.OrderId);
                });
            })
            .UseSqlite($"Data Source={databasePath}")
            .AddProjection("tests.inline", projection => projection
                .Inline<InlineReadModelProjection, ItemAdded>()));
        await using var provider = services.BuildServiceProvider();

        try
        {
            await EnsureCreatedAsync(provider);
            await AppendAsync(provider);
            await using var scope = provider.CreateAsyncScope();
            var readModel = await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>()
                .Set<InlineReadModel>()
                .SingleAsync();

            await Assert.That(readModel.Quantity).IsEqualTo(3);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Test]
    public async Task Inline_projection_failure_rolls_back_its_event_append()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        Batteries_V2.Init();
        services.AddEventLoom(eventLoom => eventLoom
            .RegisterEvent<ItemAdded>()
            .UseSingleTenancy("tenant-a")
            .UseSqlite($"Data Source={databasePath}")
            .AddProjection("tests.inline", projection => projection
                .Inline<FailingInlineProjection, ItemAdded>()));
        await using var provider = services.BuildServiceProvider();

        try
        {
            await EnsureCreatedAsync(provider);
            await Assert.That(async () => await AppendAsync(provider))
                .Throws<InvalidOperationException>();
            await using var scope = provider.CreateAsyncScope();
            var events = await scope.ServiceProvider.GetRequiredService<EventStore>()
                .ReadStreamAsync("tenant-a", "order-1");

            await Assert.That(events).IsEmpty();
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static ServiceCollection CreateServices(
        string databasePath,
        ProjectionRecorder recorder,
        ProjectionFailureRecorder? failures = null)
    {
        Batteries_V2.Init();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        if (failures is not null)
        {
            services.AddSingleton(failures);
        }

        services.AddEventLoom(eventLoom =>
        {
            eventLoom
                .RegisterEvent<ItemAdded>()
                .UseSingleTenancy("tenant-a")
                .ConfigureWorkers(options =>
                {
                    options.InstanceId = Guid.NewGuid().ToString("N");
                    options.PollInterval = TimeSpan.FromMilliseconds(10);
                    options.LeaseDuration = TimeSpan.FromSeconds(1);
                    options.LeaseRenewalInterval = TimeSpan.FromMilliseconds(100);
                    options.MaxRetryAttempts = 1;
                })
                .UseSqlite($"Data Source={databasePath}")
                .AddProjection("tests.recording", projection => projection
                    .Asynchronous<RecordingProjection, ItemAdded>());
            if (failures is not null)
            {
                eventLoom.AddProjection("tests.failing", projection => projection
                    .Asynchronous<FailingProjection, ItemAdded>());
            }
        });
        return services;
    }

    private static async Task EnsureCreatedAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.EnsureCreatedAsync();
    }

    private static async Task AppendAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EventStore>().AppendAsync(new AppendRequest(
            "tenant-a",
            "order-1",
            "order",
            ExpectedVersion.NoStream,
            [new ItemAdded(3)],
            new EventMetadata(CorrelationId: "correlation-1")));
    }

    private static async Task<ProjectionCheckpoint> WaitForCheckpointAsync(
        ServiceProvider provider,
        ProjectionKey key,
        ProjectionStatus status)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            await using var scope = provider.CreateAsyncScope();
            var checkpoint = await scope.ServiceProvider.GetRequiredService<ProjectionAdministration>()
                .GetCheckpointAsync("tenant-a", key);
            if (checkpoint?.Status == status)
            {
                return checkpoint;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        throw new TimeoutException($"Projection '{key.Name}' did not reach status '{status}'.");
    }

    [EventType("tests.projection-item-added")]
    private sealed record ItemAdded(int Quantity) : IDomainEvent<TestAggregate>;

    private sealed class TestAggregate(Guid id) : Aggregate<Guid>(id)
    {
        private void Apply(ItemAdded @event)
        {
        }
    }

    private sealed class RecordingProjection(ProjectionRecorder recorder) : IProjectionHandler<ItemAdded>
    {
        public Task HandleAsync(EventEnvelope<ItemAdded> envelope, CancellationToken cancellationToken)
        {
            recorder.Handled.TrySetResult(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingProjection(ProjectionFailureRecorder recorder) : IProjectionHandler<ItemAdded>
    {
        public Task HandleAsync(EventEnvelope<ItemAdded> envelope, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref recorder.Attempts) == 2)
            {
                recorder.Retried.TrySetResult();
            }

            throw new InvalidOperationException("projection failed");
        }
    }

    private sealed class ProjectionRecorder
    {
        public TaskCompletionSource<EventEnvelope<ItemAdded>> Handled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ProjectionFailureRecorder
    {
        public int Attempts;

        public TaskCompletionSource Retried { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class InlineReadModelProjection(EventStoreDbContext context)
        : IInlineProjectionHandler<ItemAdded>
    {
        public Task HandleAsync(EventEnvelope<ItemAdded> envelope, CancellationToken cancellationToken)
        {
            context.Set<InlineReadModel>().Add(new InlineReadModel
            {
                OrderId = envelope.StreamId,
                Quantity = envelope.Event.Quantity
            });
            return Task.CompletedTask;
        }
    }

    private sealed class FailingInlineProjection : IInlineProjectionHandler<ItemAdded>
    {
        public Task HandleAsync(EventEnvelope<ItemAdded> envelope, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("inline projection failed");
    }

    private sealed class InlineReadModel
    {
        public required string OrderId { get; set; }
        public int Quantity { get; set; }
    }
}
