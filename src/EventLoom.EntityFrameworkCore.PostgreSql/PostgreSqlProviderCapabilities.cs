namespace EventLoom.EntityFrameworkCore.PostgreSql;

/// <summary>
/// Describes EventLoom capabilities supported by PostgreSQL.
/// </summary>
public sealed class PostgreSqlProviderCapabilities : IEventStoreProviderCapabilities
{
    /// <inheritdoc />
    public bool SupportsDistributedWorkers => true;

    /// <inheritdoc />
    public bool SupportsSchemas => true;
}
