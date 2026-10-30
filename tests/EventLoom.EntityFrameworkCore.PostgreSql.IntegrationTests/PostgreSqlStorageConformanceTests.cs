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
            builder => builder
                .UsePostgreSql(database.ConnectionString)
                // Serializable appends to one tenant conflict by design; slow CI runners need more
                // retries than the default for the contention tests to be deterministic.
                .ConfigureWorkers(options => options.MaxRetryAttempts = 30),
            database.DisposeAsync);
    }
}
