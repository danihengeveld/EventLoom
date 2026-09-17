using EventLoom.Hosting;
using EventLoom.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using System.Data.Common;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

/// <summary>
/// Configures PostgreSQL storage for EventLoom.
/// </summary>
public static class EventLoomPostgreSqlBuilderExtensions
{
    /// <summary>
    /// Uses PostgreSQL as the EventLoom event-store provider.
    /// </summary>
    /// <param name="builder">The EventLoom builder to configure.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty or whitespace.</exception>
    public static EventLoomBuilder UsePostgreSql(
        this EventLoomBuilder builder,
        string connectionString) =>
        UsePostgreSql(builder, connectionString, null);

    /// <summary>
    /// Uses PostgreSQL as the EventLoom event-store provider.
    /// </summary>
    /// <param name="builder">The EventLoom builder to configure.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="configure">Optional PostgreSQL-specific EF Core configuration.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty or whitespace.</exception>
    public static EventLoomBuilder UsePostgreSql(
        this EventLoomBuilder builder,
        string connectionString,
        Action<NpgsqlDbContextOptionsBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return builder
            .AddEventStoreRetryPolicy<PostgreSqlRetryPolicy>()
            .ConfigureEventStore(options => options.UseSchema = true)
            .ConfigureDbContext(options => options.UseNpgsql(connectionString, configure));
    }

    /// <summary>
    /// Uses a scoped PostgreSQL connection shared with an application context.
    /// </summary>
    /// <remarks>
    /// This advanced overload supports <see cref="EventStore.AppendInTransactionAsync"/>.
    /// The application context must use the same connection instance for each unit of work.
    /// </remarks>
    /// <param name="builder">The EventLoom builder to configure.</param>
    /// <param name="connectionFactory">Returns the scoped connection shared with the application context.</param>
    /// <returns>The configured builder.</returns>
    public static EventLoomBuilder UsePostgreSql(
        this EventLoomBuilder builder,
        Func<IServiceProvider, DbConnection> connectionFactory) =>
        UsePostgreSql(builder, connectionFactory, null);

    /// <summary>
    /// Uses a scoped PostgreSQL connection shared with an application context.
    /// </summary>
    /// <remarks>
    /// This advanced overload supports <see cref="EventStore.AppendInTransactionAsync"/>.
    /// The application context must use the same connection instance for each unit of work.
    /// </remarks>
    /// <param name="builder">The EventLoom builder to configure.</param>
    /// <param name="connectionFactory">Returns the scoped connection shared with the application context.</param>
    /// <param name="configure">Optional PostgreSQL-specific EF Core configuration.</param>
    /// <returns>The configured builder.</returns>
    public static EventLoomBuilder UsePostgreSql(
        this EventLoomBuilder builder,
        Func<IServiceProvider, DbConnection> connectionFactory,
        Action<NpgsqlDbContextOptionsBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        return builder
            .AddEventStoreRetryPolicy<PostgreSqlRetryPolicy>()
            .ConfigureEventStore(options => options.UseSchema = true)
            .ConfigureDbContext((services, options) =>
                options.UseNpgsql(connectionFactory(services), configure));
    }
}
