using EventLoom.EntityFrameworkCore;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

public sealed class PostgreSqlProviderCapabilities : IEventStoreProviderCapabilities
{
    public bool SupportsDistributedWorkers => true;

    public bool SupportsSchemas => true;
}
