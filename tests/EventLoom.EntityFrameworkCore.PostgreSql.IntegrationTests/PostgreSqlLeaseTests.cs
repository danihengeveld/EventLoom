using EventLoom.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlLeaseTests
{
    [Test]
    public async Task PostgreSql_lease_fencing_rejects_a_stale_owner_release()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions { UseSchema = true, Schema = "eventloom_test", TablePrefix = "eventloom_" };
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseNpgsql(container.GetConnectionString()).Options,
            options);
        await context.Database.EnsureCreatedAsync();
        var leases = new WorkerLeaseStore(context, TimeProvider.System);

        var first = await leases.TryAcquireAsync("tenant-a", "orders", "node-a", TimeSpan.FromMinutes(1));
        var renewed = await leases.TryAcquireAsync("tenant-a", "orders", "node-a", TimeSpan.FromMinutes(1));

        await Assert.That(renewed!.FencingToken).IsGreaterThan(first!.FencingToken);
        await Assert.That(await leases.ReleaseAsync(first)).IsFalse();
        await Assert.That(await leases.ReleaseAsync(renewed)).IsTrue();
    }

    [Test]
    public async Task Independent_instances_do_not_acquire_the_same_active_lease()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions { UseSchema = true, Schema = "eventloom_test", TablePrefix = "eventloom_" };
        await using var setupContext = CreateContext(container.GetConnectionString(), options);
        await setupContext.Database.EnsureCreatedAsync();
        await using var firstContext = CreateContext(container.GetConnectionString(), options);
        await using var secondContext = CreateContext(container.GetConnectionString(), options);
        var first = new WorkerLeaseStore(firstContext, TimeProvider.System);
        var second = new WorkerLeaseStore(secondContext, TimeProvider.System);

        var acquisitions = await Task.WhenAll(
            first.TryAcquireAsync("tenant-a", "orders", "node-a", TimeSpan.FromMinutes(1)),
            second.TryAcquireAsync("tenant-a", "orders", "node-b", TimeSpan.FromMinutes(1)));

        await Assert.That(acquisitions.Count(value => value is not null)).IsEqualTo(1);
        await Assert.That(acquisitions.Count(value => value is null)).IsEqualTo(1);
    }

    private static EventStoreDbContext CreateContext(string connectionString, EventStoreOptions options) =>
        new(new DbContextOptionsBuilder<EventStoreDbContext>().UseNpgsql(connectionString).Options, options);
}
