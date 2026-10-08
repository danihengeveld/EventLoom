using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Storage.Conformance;

public abstract partial class StorageConformanceTests
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    [Test]
    public async Task Lease_renewal_by_the_owner_increases_the_fencing_token_and_extends_expiry()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();

        var first = await leases.TryAcquireAsync(Tenant, "projection:a", "owner-1", LeaseDuration);
        environment.Clock.Advance(TimeSpan.FromSeconds(10));
        var renewed = await leases.TryAcquireAsync(Tenant, "projection:a", "owner-1", LeaseDuration);

        await Assert.That(first).IsNotNull();
        await Assert.That(first!.TenantId).IsEqualTo(Tenant);
        await Assert.That(first.LeaseName).IsEqualTo("projection:a");
        await Assert.That(first.OwnerId).IsEqualTo("owner-1");
        await Assert.That(first.LeaseUntil).IsEqualTo(environment.Clock.GetUtcNow().AddSeconds(-10) + LeaseDuration);
        await Assert.That(renewed!.FencingToken).IsGreaterThan(first.FencingToken);
        await Assert.That(renewed.LeaseUntil).IsEqualTo(environment.Clock.GetUtcNow() + LeaseDuration);
    }

    [Test]
    public async Task Active_lease_cannot_be_taken_by_another_owner_until_it_expires()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();
        var first = await leases.TryAcquireAsync(Tenant, "projection:a", "owner-1", LeaseDuration);

        environment.Clock.Advance(LeaseDuration - TimeSpan.FromSeconds(1));
        await Assert.That(await leases.TryAcquireAsync(Tenant, "projection:a", "owner-2", LeaseDuration)).IsNull();

        environment.Clock.Advance(TimeSpan.FromSeconds(1));
        var takeover = await leases.TryAcquireAsync(Tenant, "projection:a", "owner-2", LeaseDuration);

        await Assert.That(takeover!.OwnerId).IsEqualTo("owner-2");
        await Assert.That(takeover.FencingToken).IsGreaterThan(first!.FencingToken);
        await Assert.That(await leases.ReleaseAsync(first)).IsFalse();
        await Assert.That(await leases.TryAcquireAsync(Tenant, "projection:a", "owner-1", LeaseDuration)).IsNull();
    }

    [Test]
    public async Task Only_the_current_holder_with_the_current_token_can_release_a_lease()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();
        var first = (await leases.TryAcquireAsync(Tenant, "projection:a", "owner-1", LeaseDuration))!;
        var renewed = (await leases.TryAcquireAsync(Tenant, "projection:a", "owner-1", LeaseDuration))!;

        await Assert.That(await leases.ReleaseAsync(first)).IsFalse();
        await Assert.That(await leases.ReleaseAsync(renewed with { OwnerId = "owner-2" })).IsFalse();
        await Assert.That(await leases.ReleaseAsync(renewed)).IsTrue();

        var next = await leases.TryAcquireAsync(Tenant, "projection:a", "owner-2", LeaseDuration);
        await Assert.That(next!.OwnerId).IsEqualTo("owner-2");
        await Assert.That(next.FencingToken).IsGreaterThan(renewed.FencingToken);
    }

    [Test]
    public async Task Leases_are_independent_per_tenant_and_name()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();

        await Assert.That(await leases.TryAcquireAsync(Tenant, "projection:a", "owner-1", LeaseDuration)).IsNotNull();
        await Assert.That(await leases.TryAcquireAsync(Tenant, "projection:b", "owner-2", LeaseDuration)).IsNotNull();
        await Assert.That(await leases.TryAcquireAsync("tenant-b", "projection:a", "owner-2", LeaseDuration))
            .IsNotNull();
    }

    [Test]
    public async Task Concurrent_instances_never_both_acquire_the_same_active_lease()
    {
        await using var environment = await CreateEnvironmentAsync();
        if (!environment.Capabilities.IsDistributed)
        {
            Skip.Test(
                $"{environment.Capabilities.ProviderName} is single-node; concurrency is validated by distributed providers.");
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            await using var scope = environment.CreateScope();
            var leases = scope.ServiceProvider.GetRequiredService<IWorkerLeaseStorage>();
            return await leases.TryAcquireAsync(Tenant, "projection:a", $"owner-{index}", LeaseDuration);
        }));

        await Assert.That(results.Count(value => value is not null)).IsEqualTo(1);
    }
}
