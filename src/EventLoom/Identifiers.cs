namespace EventLoom;

/// <summary>Generates identifiers for persisted events.</summary>
public interface IEventIdGenerator
{
    /// <summary>Creates a new unique event identifier.</summary>
    /// <returns>A newly generated event identifier.</returns>
    Guid Create();
}

/// <summary>Generates time-ordered UUIDv7 event identifiers.</summary>
public sealed class UuidV7EventIdGenerator : IEventIdGenerator
{
    /// <inheritdoc />
    public Guid Create() => Guid.CreateVersion7();
}
