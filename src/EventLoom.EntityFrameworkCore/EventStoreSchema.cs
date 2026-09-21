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
    /// Creates the EventLoom schema when the database has not been initialized.
    /// </summary>
    /// <remarks>
    /// This is an explicit startup operation for development, tests, and deployments
    /// that intentionally use EF Core's create-database contract. It does not migrate
    /// an existing schema and must not be registered as an automatic production
    /// startup action. Applications using deployment-managed migrations should keep
    /// those migrations in the host application.
    /// </remarks>
    /// <param name="context">The event-store context to initialize.</param>
    /// <param name="cancellationToken">A token used to cancel schema creation.</param>
    /// <returns>A task that completes when the database initialization has finished.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static Task EnsureCreatedAsync(
        EventStoreDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Database.EnsureCreatedAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the event-store table names for the configured table prefix.
    /// </summary>
    /// <param name="options">The event-store naming options.</param>
    /// <returns>The tables managed by EventLoom, in a stable order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    private static string[] GetTableNames(EventStoreOptions options)
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
        var incompatibleColumns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
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
                        .Where(column => !actualColumns.Contains(column.Name))
                        .Select(column => column.Name)
                        .ToArray();
                    if (missing.Length > 0)
                    {
                        missingColumns.Add(table.DisplayName, missing);
                    }

                    var incompatible = table.Columns
                        .Where(column => actualColumns.Contains(column.Name))
                        .Where(column =>
                        {
                            var actual = reader.GetColumnSchema()
                                .First(value => string.Equals(value.ColumnName, column.Name,
                                    StringComparison.OrdinalIgnoreCase));
                            return IsIncompatible(column, actual, context.Database.ProviderName);
                        })
                        .Select(column => column.Name)
                        .ToArray();
                    if (incompatible.Length > 0)
                    {
                        incompatibleColumns.Add(table.DisplayName, incompatible);
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

        return new EventStoreSchemaValidationResult(missingTables, missingColumns, incompatibleColumns);
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
                            .Select(property => new
                            {
                                Name = property.GetColumnName(
                                    StoreObjectIdentifier.Table(name, entityType.GetSchema())),
                                property.ClrType,
                                IsRequired = !property.IsNullable
                            })
                            .Where(column => column.Name is not null)
                            .Select(column => new ExpectedColumn(
                                column.Name!,
                                column.ClrType,
                                column.IsRequired))
                            .DistinctBy(column => column.Name, StringComparer.Ordinal)
                            .OrderBy(column => column.Name, StringComparer.Ordinal)
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

    private static bool IsIncompatible(
        ExpectedColumn expected,
        DbColumn actual,
        string? providerName)
    {
        if (expected.IsRequired && actual.AllowDBNull is true)
        {
            return true;
        }

        if (actual.DataType is null ||
            string.Equals(providerName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            return false;
        }

        var expectedType = Nullable.GetUnderlyingType(expected.ClrType) ?? expected.ClrType;
        var actualType = Nullable.GetUnderlyingType(actual.DataType) ?? actual.DataType;
        if (providerName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true &&
            expectedType == typeof(DateTimeOffset) &&
            actualType == typeof(DateTime))
        {
            return false;
        }

        return expectedType != actualType &&
               !(expectedType.IsEnum && Enum.GetUnderlyingType(expectedType) == actualType);
    }

    private sealed record ExpectedTable(
        string Name,
        string? Schema,
        IReadOnlyList<ExpectedColumn> Columns,
        string DisplayName);

    private sealed record ExpectedColumn(string Name, Type ClrType, bool IsRequired);
}

/// <summary>Reports EventLoom storage objects that are absent from the configured relational schema.</summary>
public sealed record EventStoreSchemaValidationResult(
    IReadOnlyList<string> MissingTables,
    IReadOnlyDictionary<string, IReadOnlyList<string>> MissingColumns,
    IReadOnlyDictionary<string, IReadOnlyList<string>> IncompatibleColumns)
{
    /// <summary>Gets whether every EventLoom table and mapped column is present.</summary>
    public bool IsCompatible =>
        MissingTables.Count == 0 &&
        MissingColumns.Count == 0 &&
        IncompatibleColumns.Count == 0;
}
