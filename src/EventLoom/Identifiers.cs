using System.Globalization;

namespace EventLoom;

public interface ICanonicalIdConverter<TId>
{
    string ConvertToCanonicalString(TId value);

    TId ConvertFromCanonicalString(string value);
}

public sealed class StringIdConverter : ICanonicalIdConverter<string>
{
    public string ConvertToCanonicalString(string value) => value ?? throw new ArgumentNullException(nameof(value));

    public string ConvertFromCanonicalString(string value) => value ?? throw new ArgumentNullException(nameof(value));
}

public sealed class GuidIdConverter : ICanonicalIdConverter<Guid>
{
    public string ConvertToCanonicalString(Guid value) => value.ToString("N", CultureInfo.InvariantCulture);

    public Guid ConvertFromCanonicalString(string value) =>
        Guid.TryParseExact(value, "N", out var result)
            ? result
            : throw new FormatException($"'{value}' is not a canonical GUID.");
}

public interface IEventIdGenerator
{
    Guid Create();
}

public sealed class UuidV7EventIdGenerator : IEventIdGenerator
{
    public Guid Create() => Guid.CreateVersion7();
}

public sealed class TimeProviderClock(TimeProvider timeProvider)
{
    public TimeProvider Provider { get; } = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public DateTimeOffset GetUtcNow() => Provider.GetUtcNow();
}
