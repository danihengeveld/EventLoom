using System.Globalization;

namespace EventLoom.Testing;

/// <summary>
/// Generates deterministic, monotonically increasing event identifiers for tests.
/// Replaces <see cref="UuidV7EventIdGenerator"/> so assertions on persisted event
/// identifiers do not depend on wall-clock time or randomness.
/// </summary>
public sealed class SequentialEventIdGenerator : IEventIdGenerator
{
    private long counter;

    /// <inheritdoc />
    /// <remarks>The first generated identifier is <c>00000000-0000-0000-0000-000000000001</c>.</remarks>
    public Guid Create()
    {
        var value = Interlocked.Increment(ref counter);
        return Guid.ParseExact(value.ToString("D32", CultureInfo.InvariantCulture), "N");
    }
}
