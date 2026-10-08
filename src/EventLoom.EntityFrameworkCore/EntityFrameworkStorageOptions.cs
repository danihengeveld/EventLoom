namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Controls the names and schema used by the EventLoom tables in a relational database.
/// </summary>
public sealed class EntityFrameworkStorageOptions
{
    /// <summary>
    /// Gets or sets the database schema name. The default is <c>eventloom</c>.
    /// </summary>
    public string Schema { get; set; } = "eventloom";

    /// <summary>
    /// Gets or sets the prefix applied to table names. The default is <c>eventloom_</c>.
    /// </summary>
    public string TablePrefix { get; set; } = "eventloom_";

    /// <summary>
    /// Gets or sets a value indicating whether the configured schema is used.
    /// Providers such as SQLite do not support schemas and set this to <see langword="false"/>.
    /// </summary>
    internal bool UseSchema { get; set; }
}
