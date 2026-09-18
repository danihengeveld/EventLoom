using EventLoom.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

/// <summary>
/// Provides explicit PostgreSQL event-store schema lifecycle operations.
/// </summary>
public static class PostgreSqlEventStoreSchema
{
    /// <summary>
    /// Creates the configured EventLoom schema when the database has not been initialized.
    /// </summary>
    /// <remarks>
    /// This operation is explicit and does not run automatically during provider registration.
    /// It does not migrate an existing schema. Keep production schema evolution in
    /// deployment-managed migrations owned by the host application.
    /// </remarks>
    /// <param name="context">The configured PostgreSQL event-store context.</param>
    /// <param name="cancellationToken">A token used to cancel schema creation.</param>
    /// <returns>A task that completes when schema creation has finished.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static Task EnsureCreatedAsync(
        EventStoreDbContext context,
        CancellationToken cancellationToken = default) =>
        EventStoreSchema.EnsureCreatedAsync(context, cancellationToken);
}
