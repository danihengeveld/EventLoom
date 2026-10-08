using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Storage.Conformance;

public abstract partial class StorageConformanceTests
{
    [Test]
    public async Task Latest_snapshot_is_the_highest_stream_version_for_the_snapshot_type()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<ISnapshotStorage>();

        await snapshots.WriteAsync(Snapshot(version: 5, payload: "five"), 10);
        await snapshots.WriteAsync(Snapshot(version: 9, payload: "nine"), 10);
        await snapshots.WriteAsync(Snapshot(version: 7, payload: "seven"), 10);
        await snapshots.WriteAsync(Snapshot(version: 20, payload: "other-type", type: "tests.other"), 10);

        var latest = await snapshots.ReadLatestAsync(Tenant, "order-1", "order", "tests.snapshot");

        await Assert.That(latest!.StreamVersion).IsEqualTo(9);
        await Assert.That(latest.Payload).IsEqualTo("nine");
        await Assert.That(latest.SchemaVersion).IsEqualTo(2);
        await Assert.That(latest.CreatedAt).IsEqualTo(Snapshot(1).CreatedAt);
        await Assert.That(await snapshots.ReadLatestAsync(Tenant, "order-2", "order", "tests.snapshot")).IsNull();
    }

    [Test]
    public async Task Snapshot_writes_keep_only_the_requested_number_of_recent_versions()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<ISnapshotStorage>();

        foreach (var version in new long[] { 1, 2, 3, 4 })
        {
            await snapshots.WriteAsync(Snapshot(version, $"v{version}"), 2);
        }

        await snapshots.WriteAsync(Snapshot(1, "other-stream", stream: "order-2"), 2);
        await Assert
            .That((await snapshots.ReadLatestAsync(Tenant, "order-1", "order", "tests.snapshot"))!.StreamVersion)
            .IsEqualTo(4);
        await Assert
            .That((await snapshots.ReadLatestAsync(Tenant, "order-2", "order", "tests.snapshot"))!.StreamVersion)
            .IsEqualTo(1);

        var latest = (await snapshots.ReadLatestAsync(Tenant, "order-1", "order", "tests.snapshot"))!;
        await snapshots.InvalidateAsync(latest);
        await Assert
            .That((await snapshots.ReadLatestAsync(Tenant, "order-1", "order", "tests.snapshot"))!.StreamVersion)
            .IsEqualTo(3);
        await snapshots.InvalidateAsync(
            (await snapshots.ReadLatestAsync(Tenant, "order-1", "order", "tests.snapshot"))!);
        await Assert.That(await snapshots.ReadLatestAsync(Tenant, "order-1", "order", "tests.snapshot")).IsNull();
    }

    [Test]
    public async Task Invalidation_only_removes_the_exact_snapshot_that_was_read()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<ISnapshotStorage>();
        await snapshots.WriteAsync(Snapshot(3, "current"), 5);

        await snapshots.InvalidateAsync(Snapshot(3, "stale"));

        await Assert.That((await snapshots.ReadLatestAsync(Tenant, "order-1", "order", "tests.snapshot"))!.Payload)
            .IsEqualTo("current");
    }

    [Test]
    public async Task Snapshots_are_tenant_scoped()
    {
        await using var environment = await CreateEnvironmentAsync();
        await using var scope = environment.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<ISnapshotStorage>();

        await snapshots.WriteAsync(Snapshot(3, "a"), 5);
        await snapshots.WriteAsync(Snapshot(8, "b", tenant: "tenant-b"), 5);

        await Assert.That((await snapshots.ReadLatestAsync(Tenant, "order-1", "order", "tests.snapshot"))!.Payload)
            .IsEqualTo("a");
        await Assert.That((await snapshots.ReadLatestAsync("tenant-b", "order-1", "order", "tests.snapshot"))!.Payload)
            .IsEqualTo("b");
    }

    private static SnapshotRecord Snapshot(
        long version,
        string payload = "state",
        string type = "tests.snapshot",
        string stream = "order-1",
        string tenant = Tenant) =>
        new(tenant, stream, "order", version, type, 2, payload, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
}
