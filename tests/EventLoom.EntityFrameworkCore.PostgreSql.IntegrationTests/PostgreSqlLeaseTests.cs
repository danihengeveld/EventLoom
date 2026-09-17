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
}
