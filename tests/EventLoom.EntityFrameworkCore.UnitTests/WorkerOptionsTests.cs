using EventLoom.Hosting;

namespace EventLoom.UnitTests;

public sealed class WorkerOptionsTests
{
    [Test]
    public async Task Worker_options_reject_a_renewal_interval_longer_than_the_lease()
    {
        var options = new EventStoreWorkerOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(10),
            LeaseRenewalInterval = TimeSpan.FromSeconds(10)
        };

        await Assert.That(() => options.Validate())
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Worker_options_use_distinct_process_safe_identities_by_default_and_allow_override()
    {
        var first = new EventStoreWorkerOptions();
        var second = new EventStoreWorkerOptions();
        first.InstanceId = "operator-specified-worker";

        await Assert.That(first.InstanceId).IsEqualTo("operator-specified-worker");
        await Assert.That(second.InstanceId).IsNotEqualTo(first.InstanceId);
        await Assert.That(second.InstanceId).Contains($"-{Environment.ProcessId}-");
    }

    [Test]
    public async Task Outbox_options_use_distinct_process_safe_identities_by_default_and_allow_override()
    {
        var first = new OutboxOptions();
        var second = new OutboxOptions();
        first.InstanceId = "operator-specified-publisher";

        await Assert.That(first.InstanceId).IsEqualTo("operator-specified-publisher");
        await Assert.That(second.InstanceId).IsNotEqualTo(first.InstanceId);
        await Assert.That(second.InstanceId).Contains($"-{Environment.ProcessId}-");
    }

    [Test]
    public async Task Outbox_options_reject_negative_successful_delivery_retention()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        await Assert.That(() => services.AddEventLoom(eventLoom => eventLoom
                .AddOutboxPublisher<NoopPublisher>(options =>
                    options.SuccessfulDeliveryRetention = TimeSpan.FromSeconds(-1))))
            .Throws<ArgumentOutOfRangeException>();
    }

    private sealed class NoopPublisher : IOutboxPublisher
    {
        public Task PublishAsync(EventLoom.EntityFrameworkCore.OutboxMessage message, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
