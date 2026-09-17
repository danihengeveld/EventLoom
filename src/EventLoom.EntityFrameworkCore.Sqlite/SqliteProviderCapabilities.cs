using EventLoom.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore.Sqlite;

public sealed class SqliteProviderCapabilities : IEventStoreProviderCapabilities
{
    public bool SupportsDistributedWorkers => false;

    public bool SupportsSchemas => false;

    public void ValidateWorkerConfiguration(bool distributedWorkersEnabled)
    {
        if (distributedWorkersEnabled)
        {
            throw new DistributedWorkerConfigurationException("SQLite");
        }
    }
}
