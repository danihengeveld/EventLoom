using EventLoom.Hosting;
using EventLoom.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

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
    /// <param name="configure">Optional PostgreSQL-specific EF Core configuration.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is empty or whitespace.</exception>
    public static EventLoomBuilder UsePostgreSql(
        this EventLoomBuilder builder,
        string connectionString,
        Action<NpgsqlDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return builder
            .AddEventStoreRetryPolicy<PostgreSqlRetryPolicy>()
            .ConfigureEventStore(options => options.UseSchema = true)
            .ConfigureDbContext(options => options.UseNpgsql(connectionString, configure));
    }
}
