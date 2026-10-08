using EventLoom.Storage.Conformance;
using Microsoft.Data.Sqlite;

namespace EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests;

[InheritsTests]
public sealed class SqliteStorageConformanceTests : StorageConformanceTests
{
    protected override async Task<StorageEnvironment> CreateEnvironmentAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return await StorageEnvironment.CreateAsync(
            builder => builder.UseSqlite(_ => connection),
            connection.DisposeAsync);
    }
}
