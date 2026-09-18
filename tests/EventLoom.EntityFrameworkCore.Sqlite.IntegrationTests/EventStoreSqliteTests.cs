using System.Data.Common;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EventLoom.UnitTests;

public sealed class EventStoreSqliteTests
{
    [Test]
    public async Task Sqlite_can_create_event_store_schema_and_enforce_event_uniqueness()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventStoreDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new EventStoreDbContext(
            options,
            new EventStoreOptions { TablePrefix = "test_" });

        await SqliteEventStoreSchema.EnsureCreatedAsync(context);

        var tableNames = await context.Database
            .GetDbConnection()
            .QueryAsync("select name from sqlite_master where type = 'table'");
        await Assert.That(tableNames).Contains("test_events");
        await Assert.That(tableNames).Contains("test_streams");
    }
}

internal static class SqliteConnectionExtensions
{
    public static async Task<IReadOnlyList<string>> QueryAsync(
        this DbConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
