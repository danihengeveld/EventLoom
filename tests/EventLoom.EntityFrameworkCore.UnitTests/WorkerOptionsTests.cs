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
}
