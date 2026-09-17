using Microsoft.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore;

public static class EventStoreSchema
{
    public const string MigrationsAssemblyName = "EventLoom.EntityFrameworkCore";

    public static Task MigrateAsync(
        EventStoreDbContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Database.MigrateAsync(cancellationToken);
    }

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

public interface IEventStoreProviderCapabilities
{
    bool SupportsDistributedWorkers { get; }

    bool SupportsSchemas { get; }
}

public sealed class DistributedWorkerConfigurationException(string providerName)
    : InvalidOperationException(
        $"Distributed worker mode is not supported by the configured EventLoom provider '{providerName}'.");
