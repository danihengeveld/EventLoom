using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Data.Common;

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
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty or whitespace.</exception>
    public static EventLoomBuilder UseSqlite(
        this EventLoomBuilder builder,
        string connectionString) =>
        UseSqlite(builder, connectionString, null);

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
        Action<SqliteDbContextOptionsBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        SQLitePCL.Batteries_V2.Init();

        return builder
            .ConfigureEventStore(options => options.UseSchema = false)
            .ConfigureDbContext(options => options.UseSqlite(connectionString, configure));
    }

    /// <summary>
    /// Uses a scoped SQLite connection shared with an application context.
    /// </summary>
    /// <remarks>
    /// This advanced overload supports <see cref="EventStore.AppendInTransactionAsync"/>.
    /// The application context must use the same connection instance for each unit of work.
    /// </remarks>
    /// <param name="builder">The EventLoom builder to configure.</param>
    /// <param name="connectionFactory">Returns the scoped connection shared with the application context.</param>
    /// <returns>The configured builder.</returns>
    public static EventLoomBuilder UseSqlite(
        this EventLoomBuilder builder,
        Func<IServiceProvider, DbConnection> connectionFactory) =>
        UseSqlite(builder, connectionFactory, null);

    /// <summary>
    /// Uses a scoped SQLite connection shared with an application context.
    /// </summary>
    /// <remarks>
    /// This advanced overload supports <see cref="EventStore.AppendInTransactionAsync"/>.
    /// The application context must use the same connection instance for each unit of work.
    /// </remarks>
    /// <param name="builder">The EventLoom builder to configure.</param>
    /// <param name="connectionFactory">Returns the scoped connection shared with the application context.</param>
    /// <param name="configure">Optional SQLite-specific EF Core configuration.</param>
    /// <returns>The configured builder.</returns>
    public static EventLoomBuilder UseSqlite(
        this EventLoomBuilder builder,
        Func<IServiceProvider, DbConnection> connectionFactory,
        Action<SqliteDbContextOptionsBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        SQLitePCL.Batteries_V2.Init();

        return builder
            .ConfigureEventStore(options => options.UseSchema = false)
            .ConfigureDbContext((services, options) =>
                options.UseSqlite(connectionFactory(services), configure));
    }
}
