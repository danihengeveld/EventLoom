using EventLoom.Storage.Conformance;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

[InheritsTests]
public sealed class PostgreSqlStorageConformanceTests : StorageConformanceTests
{
    [ClassDataSource<PostgreSqlTestServer>(Shared = SharedType.PerAssembly)]
    public required PostgreSqlTestServer Server { get; init; }

    protected override async Task<StorageEnvironment> CreateEnvironmentAsync()
    {
        var database = await Server.CreateDatabaseAsync(initializeSchema: false);
        return await StorageEnvironment.CreateAsync(
            builder => builder.UsePostgreSql(database.ConnectionString),
            database.DisposeAsync);
    }
}
