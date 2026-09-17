using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Provides migration and schema inspection helpers for the EventLoom event store.
/// </summary>
public static class EventStoreSchema
{
    /// <summary>
    /// Gets the name of the assembly containing EventLoom's migrations.
    /// </summary>
    public const string MigrationsAssemblyName = "EventLoom.EntityFrameworkCore";

    /// <summary>
    /// Applies pending EF Core migrations for the event-store context.
    /// </summary>
    /// <param name="context">The event-store context to migrate.</param>
    /// <param name="cancellationToken">A token used to cancel the migration.</param>
    /// <returns>A task that completes when all pending migrations have been applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static Task MigrateAsync(
        EventStoreDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the event-store table names for the configured table prefix.
    /// </summary>
    /// <param name="options">The event-store naming options.</param>
    /// <returns>The tables managed by EventLoom, in a stable order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static string[] GetTableNames(EventStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return
        [
            $"{options.TablePrefix}events",
            $"{options.TablePrefix}outbox",
            $"{options.TablePrefix}outbox_attempts",
            $"{options.TablePrefix}offsets",
            $"{options.TablePrefix}projection_checkpoints",
            $"{options.TablePrefix}projection_failures",
            $"{options.TablePrefix}projection_leases",
            $"{options.TablePrefix}snapshots",
            $"{options.TablePrefix}streams"
        ];
    }

    /// <summary>
    /// Verifies that the configured database contains the EventLoom tables and columns required by its EF Core model.
    /// </summary>
    /// <remarks>
    /// This check is read-only and does not apply migrations or create a database. It supports the relational
    /// providers supported by EventLoom and deliberately reports only configured table and column names.
    /// </remarks>
    /// <param name="context">The configured EventLoom context.</param>
    /// <param name="cancellationToken">A token used to cancel validation.</param>
    /// <returns>A payload-safe compatibility result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="DbException">The database cannot be queried.</exception>
    public static async Task<EventStoreSchemaValidationResult> ValidateAsync(
        EventStoreDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tables = GetExpectedTables(context);
        var missingTables = new List<string>();
        var missingColumns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var connection = context.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            foreach (var table in tables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {Quote(table.Schema, table.Name)} WHERE 1 = 0";
                DbDataReader reader;
                try
                {
                    reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellationToken);
                }
                catch (DbException)
                {
                    missingTables.Add(table.DisplayName);
                    continue;
                }

                await using (reader)
                {
                    var actualColumns = reader.GetColumnSchema()
                        .Select(column => column.ColumnName)
                        .Where(column => column is not null)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var missing = table.Columns
                        .Where(column => !actualColumns.Contains(column))
                        .ToArray();
                    if (missing.Length > 0)
                    {
                        missingColumns.Add(table.DisplayName, missing);
                    }
                }
            }
        }
        finally
        {
            if (closeConnection)
            {
                await connection.CloseAsync();
            }
        }

        return new EventStoreSchemaValidationResult(missingTables, missingColumns);
    }

    private static IReadOnlyList<ExpectedTable> GetExpectedTables(EventStoreDbContext context)
    {
        var eventStoreTables = GetTableNames(context.Configuration)
            .ToHashSet(StringComparer.Ordinal);
        return context.Model.GetEntityTypes()
            .Select(entityType =>
            {
                var name = entityType.GetTableName();
                return name is null || !eventStoreTables.Contains(name)
                    ? null
                    : new
                    {
                        Name = name,
                        Schema = entityType.GetSchema(),
                        Columns = entityType.GetProperties()
                            .Select(property => property.GetColumnName(
                                StoreObjectIdentifier.Table(name, entityType.GetSchema())))
                            .Where(column => column is not null)
                            .Cast<string>()
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(column => column, StringComparer.Ordinal)
                            .ToArray()
                    };
            })
            .Where(table => table is not null)
            .Select(table => new ExpectedTable(
                table!.Name,
                table.Schema,
                table.Columns,
                table.Schema is null ? table.Name : $"{table.Schema}.{table.Name}"))
            .OrderBy(table => table.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static string Quote(string? schema, string table) =>
        schema is null
            ? QuoteIdentifier(table)
            : $"{QuoteIdentifier(schema)}.{QuoteIdentifier(table)}";

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record ExpectedTable(
        string Name,
        string? Schema,
        IReadOnlyList<string> Columns,
        string DisplayName);
}

/// <summary>Reports EventLoom storage objects that are absent from the configured relational schema.</summary>
public sealed record EventStoreSchemaValidationResult(
    IReadOnlyList<string> MissingTables,
    IReadOnlyDictionary<string, IReadOnlyList<string>> MissingColumns)
{
    /// <summary>Gets whether every EventLoom table and mapped column is present.</summary>
    public bool IsCompatible => MissingTables.Count == 0 && MissingColumns.Count == 0;
}

/// <summary>
/// Describes storage-provider features used by EventLoom.
/// </summary>
public interface IEventStoreProviderCapabilities
{
    /// <summary>
    /// Gets a value indicating whether the provider supports distributed workers.
    /// </summary>
    bool SupportsDistributedWorkers { get; }

    /// <summary>
    /// Gets a value indicating whether the provider supports database schemas.
    /// </summary>
    bool SupportsSchemas { get; }
}

/// <summary>
/// Indicates that distributed worker mode was requested for an unsupported provider.
/// </summary>
/// <param name="providerName">The provider name included in the exception message.</param>
public sealed class DistributedWorkerConfigurationException(string providerName)
    : InvalidOperationException(
        $"Distributed worker mode is not supported by the configured EventLoom provider '{providerName}'.");
