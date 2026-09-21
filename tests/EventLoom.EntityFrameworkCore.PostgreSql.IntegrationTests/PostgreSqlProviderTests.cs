using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlProviderTests
{
    [Test]
    public async Task Event_store_schema_helper_creates_configured_schema_and_tables()
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

        await EventStoreSchema.EnsureCreatedAsync(context);

        var validation = await EventStoreSchema.ValidateAsync(context);
        await Assert.That(validation.IsCompatible).IsTrue();
        await Assert.That(validation.MissingTables).IsEmpty();
    }
}
