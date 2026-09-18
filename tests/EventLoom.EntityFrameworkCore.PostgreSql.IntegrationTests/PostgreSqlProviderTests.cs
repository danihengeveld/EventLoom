using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

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

    [Test]
    public async Task PostgreSql_schema_helper_creates_configured_schema_and_tables()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await container.StartAsync();
        var options = new EventStoreOptions
        {
            UseSchema = true,
            Schema = "eventloom_provider_test",
            TablePrefix = "custom_"
        };
        await using var context = new EventStoreDbContext(
            new DbContextOptionsBuilder<EventStoreDbContext>()
                .UseNpgsql(container.GetConnectionString())
                .Options,
            options);

        await PostgreSqlEventStoreSchema.EnsureCreatedAsync(context);

        var validation = await EventStoreSchema.ValidateAsync(context);
        await Assert.That(validation.IsCompatible).IsTrue();
        await Assert.That(validation.MissingTables).IsEmpty();
    }
}
