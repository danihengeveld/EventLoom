namespace EventLoom;

public enum ExpectedVersionKind
{
    Exact,
    NoStream,
    StreamExists,
    Any
}

public readonly record struct ExpectedVersion
{
    private ExpectedVersion(ExpectedVersionKind kind, long? value)
    {
        Kind = kind;
        Value = value;
    }

    public ExpectedVersionKind Kind { get; }

    public long? Value { get; }

    public static ExpectedVersion Exact(long version) =>
        version >= 0
            ? new(ExpectedVersionKind.Exact, version)
            : throw new ArgumentOutOfRangeException(nameof(version));

    public static ExpectedVersion NoStream { get; } = new(ExpectedVersionKind.NoStream, null);

    public static ExpectedVersion StreamExists { get; } = new(ExpectedVersionKind.StreamExists, null);

    public static ExpectedVersion Any { get; } = new(ExpectedVersionKind.Any, null);

    public bool IsMatch(long? currentVersion) => Kind switch
    {
        ExpectedVersionKind.Exact => currentVersion == Value,
        ExpectedVersionKind.NoStream => currentVersion is null,
        ExpectedVersionKind.StreamExists => currentVersion is not null,
        ExpectedVersionKind.Any => true,
        _ => false
    };
}
