using System.Data.Common;
using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

/// <summary>
/// Configures PostgreSQL storage for EventLoom.
/// </summary>
public static class EventLoomPostgreSqlBuilderExtensions
{
    /// <param name="builder">The EventLoom builder to configure.</param>
    extension(EventLoomBuilder builder)
    {
        /// <summary>
        /// Uses PostgreSQL as the EventLoom event-store provider.
        /// </summary>
        /// <param name="connectionString">The PostgreSQL connection string.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty or whitespace.</exception>
        public EventLoomBuilder UsePostgreSql(string connectionString) =>
            UsePostgreSql(builder, connectionString, null);

        /// <summary>
        /// Uses PostgreSQL as the EventLoom event-store provider.
        /// </summary>
        /// <param name="connectionString">The PostgreSQL connection string.</param>
        /// <param name="configure">Optional PostgreSQL-specific EF Core configuration.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty or whitespace.</exception>
        public EventLoomBuilder UsePostgreSql(string connectionString,
            Action<NpgsqlDbContextOptionsBuilder>? configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

            return Use(builder, (_, options) => options.UseNpgsql(connectionString, configure));
        }

        /// <summary>
        /// Uses a scoped PostgreSQL connection shared with an application context.
        /// </summary>
        /// <remarks>
        /// This advanced overload lets application contexts join an EventLoom unit of work through
        /// <c>EnlistAsync</c>. The application context must use the same connection instance for each unit of work.
        /// </remarks>
        /// <param name="connectionFactory">Returns the scoped connection shared with the application context.</param>
        /// <returns>The configured builder.</returns>
        public EventLoomBuilder UsePostgreSql(Func<IServiceProvider, DbConnection> connectionFactory) =>
            UsePostgreSql(builder, connectionFactory, null);

        /// <summary>
        /// Uses a scoped PostgreSQL connection shared with an application context.
        /// </summary>
        /// <remarks>
        /// This advanced overload lets application contexts join an EventLoom unit of work through
        /// <c>EnlistAsync</c>. The application context must use the same connection instance for each unit of work.
        /// </remarks>
        /// <param name="connectionFactory">Returns the scoped connection shared with the application context.</param>
        /// <param name="configure">Optional PostgreSQL-specific EF Core configuration.</param>
        /// <returns>The configured builder.</returns>
        public EventLoomBuilder UsePostgreSql(Func<IServiceProvider, DbConnection> connectionFactory,
            Action<NpgsqlDbContextOptionsBuilder>? configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(connectionFactory);

            return Use(builder, (services, options) =>
                options.UseNpgsql(connectionFactory(services), configure));
        }
    }

    private static EventLoomBuilder Use(
        EventLoomBuilder builder,
        Action<IServiceProvider, DbContextOptionsBuilder> configure)
    {
        builder.AddEventStoreRetryPolicy<PostgreSqlRetryPolicy>();
        return EventLoomEntityFrameworkBuilderExtensions.UseEntityFramework(
            builder,
            "PostgreSQL",
            isDistributed: true,
            useSchema: true,
            new PostgreSqlStorageDialect(),
            configure);
    }
}
