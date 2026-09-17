using System.Globalization;

namespace EventLoom;

/// <summary>Converts an application identifier to and from its canonical persisted string form.</summary>
public interface ICanonicalIdConverter<TId>
{
    /// <summary>Converts an identifier to the stable string stored by EventLoom.</summary>
    /// <param name="value">The application identifier.</param>
    /// <returns>The canonical persisted string.</returns>
    string ConvertToCanonicalString(TId value);

    /// <summary>Converts a canonical persisted string back to an application identifier.</summary>
    /// <param name="value">The canonical persisted string.</param>
    /// <returns>The application identifier.</returns>
    TId ConvertFromCanonicalString(string value);
}

/// <summary>Provides canonical conversion for string identifiers.</summary>
public sealed class StringIdConverter : ICanonicalIdConverter<string>
{
    /// <inheritdoc />
    public string ConvertToCanonicalString(string value) => value ?? throw new ArgumentNullException(nameof(value));

    /// <inheritdoc />
    public string ConvertFromCanonicalString(string value) => value ?? throw new ArgumentNullException(nameof(value));
}

/// <summary>Provides canonical, 32-character conversion for GUID identifiers.</summary>
public sealed class GuidIdConverter : ICanonicalIdConverter<Guid>
{
    /// <inheritdoc />
    public string ConvertToCanonicalString(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public Guid ConvertFromCanonicalString(string value) =>
        Guid.TryParseExact(value, "N", out var result)
            ? result
            : throw new FormatException($"'{value}' is not a canonical GUID.");
}

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

/// <summary>Exposes an injected time provider for deterministic application behavior.</summary>
public sealed class TimeProviderClock(TimeProvider timeProvider)
{
    /// <summary>Gets the time provider used by the clock.</summary>
    public TimeProvider Provider { get; } = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Gets the current UTC time from the configured provider.</summary>
    /// <returns>The current UTC time.</returns>
    public DateTimeOffset GetUtcNow() => Provider.GetUtcNow();
}
