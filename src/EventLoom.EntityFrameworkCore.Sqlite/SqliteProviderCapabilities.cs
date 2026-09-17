using EventLoom.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore.Sqlite;

/// <summary>
/// Describes EventLoom capabilities supported by SQLite.
/// </summary>
public sealed class SqliteProviderCapabilities : IEventStoreProviderCapabilities
{
    /// <inheritdoc />
    public bool SupportsDistributedWorkers => false;

    /// <inheritdoc />
    public bool SupportsSchemas => false;

    /// <summary>
    /// Validates whether distributed worker mode can be enabled for SQLite.
    /// </summary>
    /// <param name="distributedWorkersEnabled">Whether distributed worker mode was requested.</param>
    /// <exception cref="DistributedWorkerConfigurationException">Distributed worker mode is enabled.</exception>
    public void ValidateWorkerConfiguration(bool distributedWorkersEnabled)
    {
        if (distributedWorkersEnabled)
        {
            throw new DistributedWorkerConfigurationException("SQLite");
        }
    }
}
