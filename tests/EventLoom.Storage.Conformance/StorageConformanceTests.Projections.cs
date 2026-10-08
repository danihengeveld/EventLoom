using EventLoom.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Storage.Conformance;

public abstract partial class StorageConformanceTests
{
    private static readonly ProjectionKey OrdersProjection = new("tests.orders", 1);

    [Test]
    public async Task Processing_runs_the_handler_and_advances_the_checkpoint()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();
        var applied = 0;

        var result = await projections.ProcessAsync(
            Tenant, OrdersProjection, envelope, lease, (_, _) => { applied++; return Task.CompletedTask; });

        var checkpoint = await projections.GetCheckpointAsync(Tenant, OrdersProjection);
        await Assert.That(result).IsEqualTo(ProjectionDeliveryResult.Processed);
        await Assert.That(applied).IsEqualTo(1);
        await Assert.That(checkpoint!.TenantOffset).IsEqualTo(envelope.TenantOffset);
        await Assert.That(checkpoint.Status).IsEqualTo(ProjectionStatus.Running);
        await Assert.That(checkpoint.Key).IsEqualTo(OrdersProjection);
        await Assert.That(checkpoint.UpdatedAt).IsEqualTo(environment.Clock.GetUtcNow());
    }

    [Test]
    public async Task Redelivery_of_a_processed_event_does_not_run_the_handler_again()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();
        var applied = 0;
        Task Apply(IProjectionTransactionContext _, CancellationToken __)
        {
            applied++;
            return Task.CompletedTask;
        }

        await projections.ProcessAsync(Tenant, OrdersProjection, envelope, lease, Apply);
        var again = await projections.ProcessAsync(Tenant, OrdersProjection, envelope, lease, Apply);

        await Assert.That(again).IsEqualTo(ProjectionDeliveryResult.AlreadyProcessed);
        await Assert.That(applied).IsEqualTo(1);
    }

    [Test]
    public async Task A_failing_handler_leaves_no_checkpoint()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();

        await Assert.That(async () => await projections.ProcessAsync(
                Tenant, OrdersProjection, envelope, lease,
                (_, _) => throw new InvalidOperationException("handler failed")))
            .Throws<InvalidOperationException>();

        await Assert.That(await projections.GetCheckpointAsync(Tenant, OrdersProjection)).IsNull();
    }

    [Test]
    public async Task A_stale_lease_cannot_advance_a_checkpoint()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();
        var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();
        environment.Clock.Advance(LeaseDuration);
        await leases.TryAcquireAsync(Tenant, lease.LeaseName, "owner-2", LeaseDuration);

        await Assert.That(async () => await projections.ProcessAsync(
                Tenant, OrdersProjection, envelope, lease, (_, _) => Task.CompletedTask))
            .Throws<ProjectionLeaseLostException>();
        await Assert.That(async () => await projections.RecordFailureAsync(
                Tenant, OrdersProjection, envelope, lease, 1, "System.InvalidOperationException"))
            .Throws<ProjectionLeaseLostException>();

        await Assert.That(await projections.GetCheckpointAsync(Tenant, OrdersProjection)).IsNull();
    }

    [Test]
    public async Task An_expired_lease_cannot_advance_a_checkpoint_even_when_nobody_took_over()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();
        environment.Clock.Advance(LeaseDuration);

        await Assert.That(async () => await projections.ProcessAsync(
                Tenant, OrdersProjection, envelope, lease, (_, _) => Task.CompletedTask))
            .Throws<ProjectionLeaseLostException>();
    }

    [Test]
    public async Task A_recorded_failure_pauses_the_projection_until_it_is_resumed()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();
        await projections.RecordFailureAsync(
            Tenant, OrdersProjection, envelope, lease, 3, "System.InvalidOperationException");

        var paused = await projections.ProcessAsync(
            Tenant, OrdersProjection, envelope, lease, (_, _) => Task.CompletedTask);
        var failures = await projections.ReadFailuresAsync(Tenant, OrdersProjection, false);

        await Assert.That(paused).IsEqualTo(ProjectionDeliveryResult.Paused);
        await Assert.That((await projections.GetCheckpointAsync(Tenant, OrdersProjection))!.Status)
            .IsEqualTo(ProjectionStatus.Paused);
        var failure = failures.Single();
        await Assert.That(failure.EventId).IsEqualTo(envelope.EventId);
        await Assert.That(failure.EventType).IsEqualTo(envelope.EventType);
        await Assert.That(failure.TenantOffset).IsEqualTo(envelope.TenantOffset);
        await Assert.That(failure.AttemptCount).IsEqualTo(3);
        await Assert.That(failure.ExceptionType).IsEqualTo("System.InvalidOperationException");
        await Assert.That(failure.ResolvedAt).IsNull();
        await Assert.That(await projections.CountUnresolvedFailuresAsync([OrdersProjection])).IsEqualTo(1);

        await Assert.That(await projections.ResumeAsync(Tenant, OrdersProjection)).IsTrue();
        await Assert.That(await projections.ResumeAsync(Tenant, OrdersProjection)).IsFalse();
        var resumed = await projections.ProcessAsync(
            Tenant, OrdersProjection, envelope, lease, (_, _) => Task.CompletedTask);

        await Assert.That(resumed).IsEqualTo(ProjectionDeliveryResult.Processed);
        await Assert.That(await projections.CountUnresolvedFailuresAsync([OrdersProjection])).IsEqualTo(0);
        await Assert.That(await projections.ReadFailuresAsync(Tenant, OrdersProjection, false)).IsEmpty();
        var resolved = (await projections.ReadFailuresAsync(Tenant, OrdersProjection, true)).Single();
        await Assert.That(resolved.ResolvedAt).IsNotNull();
        await Assert.That(resolved.WasSkipped).IsFalse();
    }

    [Test]
    public async Task Repeated_failures_for_one_event_accumulate_into_a_single_failure_record()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();

        await projections.RecordFailureAsync(Tenant, OrdersProjection, envelope, lease, 2, "A.First");
        await projections.RecordFailureAsync(Tenant, OrdersProjection, envelope, lease, 1, "B.Second");

        var failure = (await projections.ReadFailuresAsync(Tenant, OrdersProjection, false)).Single();
        await Assert.That(failure.AttemptCount).IsEqualTo(3);
        await Assert.That(failure.ExceptionType).IsEqualTo("B.Second");
    }

    [Test]
    public async Task Skipping_a_failed_event_advances_past_it_and_marks_the_failure_skipped()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();

        await Assert.That(await projections.SkipAsync(Tenant, OrdersProjection, envelope.EventId)).IsFalse();
        await projections.RecordFailureAsync(Tenant, OrdersProjection, envelope, lease, 1, "A.Failure");

        await Assert.That(await projections.SkipAsync(Tenant, OrdersProjection, Guid.NewGuid())).IsFalse();
        await Assert.That(await projections.SkipAsync(Tenant, OrdersProjection, envelope.EventId)).IsTrue();

        var checkpoint = await projections.GetCheckpointAsync(Tenant, OrdersProjection);
        var failure = (await projections.ReadFailuresAsync(Tenant, OrdersProjection, true)).Single();
        await Assert.That(checkpoint!.TenantOffset).IsEqualTo(envelope.TenantOffset);
        await Assert.That(checkpoint.Status).IsEqualTo(ProjectionStatus.Running);
        await Assert.That(failure.WasSkipped).IsTrue();
        await Assert.That(failure.ResolvedAt).IsNotNull();
    }

    [Test]
    public async Task Replay_resets_the_checkpoint_so_every_event_is_delivered_again()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();
        await projections.ProcessAsync(Tenant, OrdersProjection, envelope, lease, (_, _) => Task.CompletedTask);

        await projections.ReplayAsync(Tenant, OrdersProjection);
        var checkpoint = await projections.GetCheckpointAsync(Tenant, OrdersProjection);
        var again = await projections.ProcessAsync(
            Tenant, OrdersProjection, envelope, lease, (_, _) => Task.CompletedTask);

        await Assert.That(checkpoint!.TenantOffset).IsEqualTo(0);
        await Assert.That(checkpoint.Status).IsEqualTo(ProjectionStatus.Running);
        await Assert.That(again).IsEqualTo(ProjectionDeliveryResult.Processed);
    }

    [Test]
    public async Task Checkpoints_are_independent_per_projection_version_and_tenant()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var (envelope, lease) = await ArrangeProjectionAsync(scope.ServiceProvider);
        var projections = scope.ServiceProvider.GetRequiredService<IProjectionStorage>();
        var other = new ProjectionKey("tests.orders", 2);

        await projections.ProcessAsync(Tenant, OrdersProjection, envelope, lease, (_, _) => Task.CompletedTask);

        await Assert.That(await projections.GetCheckpointAsync(Tenant, other)).IsNull();
        await Assert.That(await projections.GetCheckpointAsync("tenant-b", OrdersProjection)).IsNull();
        var checkpoints = await projections.ReadCheckpointsAsync([OrdersProjection, other]);
        await Assert.That(checkpoints.Single().Key).IsEqualTo(OrdersProjection);
        await Assert.That(await projections.ReadCheckpointsAsync([other])).IsEmpty();
    }

    private static async Task<(EventEnvelope Envelope, WorkerLease Lease)> ArrangeProjectionAsync(
        IServiceProvider services)
    {
        var events = services.GetRequiredService<IEventStorage>();
        var stored = (await events.AppendAsync(Request("order-1", ExpectedVersion.NoStream, 1), null)).Events.Single();
        var lease = await services.GetRequiredService<IWorkerLeaseStorage>()
            .TryAcquireAsync(Tenant, "projection:tests.orders:1", "owner-1", LeaseDuration);
        var envelope = new EventEnvelope(
            stored.EventId,
            stored.EventType,
            stored.EventTypeVersion,
            stored.StreamId,
            stored.AggregateType,
            stored.StreamVersion,
            stored.TenantOffset,
            new TenantId(stored.TenantId),
            stored.OccurredAt,
            new object(),
            stored.Metadata);
        return (envelope, lease!);
    }
}
