using Microsoft.EntityFrameworkCore;

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
            $"{options.TablePrefix}positions",
            $"{options.TablePrefix}projection_checkpoints",
            $"{options.TablePrefix}projection_failures",
            $"{options.TablePrefix}projection_leases",
            $"{options.TablePrefix}snapshots",
            $"{options.TablePrefix}streams"
        ];
    }
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
