using EventLoom.EntityFrameworkCore.PostgreSql;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlProviderTests
{
    [Test]
    public async Task PostgreSql_advertises_distributed_worker_and_schema_support()
    {
        var capabilities = new PostgreSqlProviderCapabilities();

        await Assert.That(capabilities.SupportsDistributedWorkers).IsTrue();
        await Assert.That(capabilities.SupportsSchemas).IsTrue();
    }
}
