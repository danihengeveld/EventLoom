using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests;

public sealed class WorkerLeaseTests
{
    [Test]
    public async Task Lease_acquisition_renews_with_a_monotonic_fencing_token_and_release_is_fenced()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            new EventStoreOptions { TablePrefix = "test_" });
        await context.Database.EnsureCreatedAsync();
        var leases = new WorkerLeaseStore(context, TimeProvider.System);

        var first = await leases.TryAcquireAsync("tenant-a", "orders", "node-a", TimeSpan.FromMinutes(1));
        var second = await leases.TryAcquireAsync("tenant-a", "orders", "node-a", TimeSpan.FromMinutes(1));
        var blocked = await leases.TryAcquireAsync("tenant-a", "orders", "node-b", TimeSpan.FromMinutes(1));

        await Assert.That(first).IsNotNull();
        await Assert.That(second!.FencingToken).IsGreaterThan(first!.FencingToken);
        await Assert.That(blocked).IsNull();
        await Assert.That(await leases.ReleaseAsync(first)).IsFalse();
        await Assert.That(await leases.ReleaseAsync(second)).IsTrue();
    }
}
