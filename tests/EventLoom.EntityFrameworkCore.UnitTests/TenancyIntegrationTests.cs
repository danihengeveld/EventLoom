using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore.UnitTests;

public sealed class TenancyIntegrationTests
{
    [Test]
    public async Task Required_tenancy_rejects_conflicting_scope_before_database_access()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>().UseSqlite(connection).Options);
        var store = new EventStore(
            context,
            new EventSerializer(new EventRegistry()),
            new UuidV7EventIdGenerator(),
            TimeProvider.System,
            new EventStoreOptions { TenancyMode = TenancyMode.Required },
            new TestTenantAccessor(new TenantId("acme")));

        await Assert.That(async () => await store.ReadStreamAsync("other", "stream"))
            .Throws<InvalidOperationException>();
    }

    private sealed class TestTenantAccessor(TenantId? tenantId) : ITenantAccessor
    {
        public TenantId? TenantId { get; } = tenantId;
    }
}
