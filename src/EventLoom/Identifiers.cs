using System.Globalization;

namespace EventLoom;

/// <summary>Converts an application identifier to and from its canonical persisted string form.</summary>
public interface ICanonicalIdConverter<TId>
{
    string ConvertToCanonicalString(TId value);

    TId ConvertFromCanonicalString(string value);
}

/// <summary>Provides canonical conversion for string identifiers.</summary>
public sealed class StringIdConverter : ICanonicalIdConverter<string>
{
    public string ConvertToCanonicalString(string value) => value ?? throw new ArgumentNullException(nameof(value));

    public string ConvertFromCanonicalString(string value) => value ?? throw new ArgumentNullException(nameof(value));
}

/// <summary>Provides canonical, 32-character conversion for GUID identifiers.</summary>
public sealed class GuidIdConverter : ICanonicalIdConverter<Guid>
{
    public string ConvertToCanonicalString(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    public Guid ConvertFromCanonicalString(string value) =>
        Guid.TryParseExact(value, "N", out var result)
            ? result
            : throw new FormatException($"'{value}' is not a canonical GUID.");
}

/// <summary>Generates identifiers for persisted events.</summary>
public interface IEventIdGenerator
{
    Guid Create();
}

/// <summary>Generates time-ordered UUIDv7 event identifiers.</summary>
public sealed class UuidV7EventIdGenerator : IEventIdGenerator
{
    public Guid Create() => Guid.CreateVersion7();
}

/// <summary>Exposes an injected time provider for deterministic application behavior.</summary>
public sealed class TimeProviderClock(TimeProvider timeProvider)
{
    public TimeProvider Provider { get; } = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public DateTimeOffset GetUtcNow() => Provider.GetUtcNow();
}
