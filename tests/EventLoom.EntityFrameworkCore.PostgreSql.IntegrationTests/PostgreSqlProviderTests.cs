namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public sealed class PostgreSqlProviderTests : PostgreSqlIntegrationTest
{
    [Test]
    public async Task Event_store_schema_helper_creates_configured_schema_and_tables()
    {
        var options = new EventStoreOptions
        {
            UseSchema = true,
            Schema = "eventloom_provider_test",
            TablePrefix = "custom_"
        };
        await using var database = await Server.CreateDatabaseAsync(options, initializeSchema: false);
        await using var context = database.CreateContext();

        await EventStoreSchema.EnsureCreatedAsync(context);

        var validation = await EventStoreSchema.ValidateAsync(context);
        await Assert.That(validation.IsCompatible).IsTrue();
        await Assert.That(validation.MissingTables).IsEmpty();
    }
}
