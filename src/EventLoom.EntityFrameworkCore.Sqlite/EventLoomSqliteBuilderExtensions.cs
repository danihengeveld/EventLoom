using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EventLoom.EntityFrameworkCore.Sqlite;

/// <summary>
/// Configures SQLite storage for EventLoom.
/// </summary>
public static class EventLoomSqliteBuilderExtensions
{
    /// <summary>
    /// Uses SQLite as the EventLoom event-store provider.
    /// </summary>
    /// <param name="builder">The EventLoom builder to configure.</param>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="configure">Optional SQLite-specific EF Core configuration.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty or whitespace.</exception>
    public static EventLoomBuilder UseSqlite(
        this EventLoomBuilder builder,
        string connectionString,
        Action<SqliteDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());

        return builder
            .ConfigureEventStore(options => options.UseSchema = false)
            .ConfigureDbContext(options => options.UseSqlite(connectionString, configure));
    }
}
