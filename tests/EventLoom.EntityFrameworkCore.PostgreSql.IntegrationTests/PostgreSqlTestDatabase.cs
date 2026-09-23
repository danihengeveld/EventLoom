using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests;

public abstract class PostgreSqlIntegrationTest
{
    [ClassDataSource<PostgreSqlTestServer>(Shared = SharedType.PerAssembly)]
    public required PostgreSqlTestServer Server { get; init; }
}

public sealed class PostgreSqlTestServer : IAsyncInitializer, IAsyncDisposable
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    internal async Task<PostgreSqlTestDatabase> CreateDatabaseAsync(
        EventStoreOptions? options = null,
        bool initializeSchema = true)
    {
        var name = $"eventloom_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        var database = new PostgreSqlTestDatabase(this, name, options ?? new EventStoreOptions
        {
            UseSchema = true,
            Schema = "eventloom_test",
            TablePrefix = "eventloom_"
        });
        try
        {
            if (initializeSchema)
            {
                await using var context = database.CreateContext();
                if (!await context.Database.EnsureCreatedAsync())
                {
                    throw new InvalidOperationException("The PostgreSQL test database was not empty.");
                }
            }

            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    internal string ConnectionStringFor(string name)
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = name,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    internal async Task DropDatabaseAsync(string name)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE \"{name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private string AdminConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
            {
                Pooling = false
            };
            return builder.ConnectionString;
        }
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}

internal sealed class PostgreSqlTestDatabase(
    PostgreSqlTestServer server,
    string name,
    EventStoreOptions options) : IAsyncDisposable
{
    public EventStoreOptions Options { get; } = options;

    public EventStoreDbContext CreateContext() =>
        new(
            new DbContextOptionsBuilder<EventStoreDbContext>()
                .UseNpgsql(server.ConnectionStringFor(name))
                .Options,
            Options);

    public ValueTask DisposeAsync() => new(server.DropDatabaseAsync(name));
}
