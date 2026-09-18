using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class TenancyIntegrationTests
{
    [Test]
    public async Task Multi_tenancy_rejects_conflicting_scope_before_database_access()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options);
        var store = new EventStore(
            context,
            new EventSerializer(new EventRegistry()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            new EventStoreOptions { TenancyMode = TenancyMode.MultiTenant },
            new TestTenantAccessor(new TenantId("acme")));

        await Assert.That(async () => await store.ReadStreamAsync("other", "stream"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Explicit_background_tenant_reads_do_not_require_a_request_scope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new EventStoreOptions { TenancyMode = TenancyMode.MultiTenant };
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options,
            options);
        await context.Database.EnsureCreatedAsync();
        var registry = new EventRegistry().RegisterEvent<ItemAdded>();
        var requestStore = new EventStore(
            context,
            new EventSerializer(registry),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            options,
            new TestTenantAccessor(new TenantId("acme")));
        await requestStore.AppendAsync(new AppendRequest(
            "acme",
            "order-1",
            "order",
            ExpectedVersion.NoStream,
            [new ItemAdded()],
            new EventMetadata()));
        var backgroundStore = new EventStore(
            context,
            new EventSerializer(registry),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            options);

        var events = await backgroundStore.ReadTenantOffsetsForBackgroundAsync("acme");

        await Assert.That(events.Single().TenantId!.Value.Value).IsEqualTo("acme");
    }

    [EventType("tests.background-item-added")]
    private sealed record ItemAdded : IDomainEvent;

    private sealed class TestTenantAccessor(TenantId? tenantId) : ITenantAccessor
    {
        public TenantId? TenantId { get; } = tenantId;
    }
}
