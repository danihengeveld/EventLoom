namespace EventLoom;

/// <summary>Describes how an append validates the current stream version.</summary>
public enum ExpectedVersionKind
{
    /// <summary>Requires one exact current stream version.</summary>
    Exact,

    /// <summary>Requires that no stream exists.</summary>
    NoStream,

    /// <summary>Requires that a stream exists at any version.</summary>
    StreamExists,

    /// <summary>Does not impose an application-level stream version requirement.</summary>
    Any
}

/// <summary>Represents an append version expectation.</summary>
public readonly record struct ExpectedVersion
{
    private ExpectedVersion(ExpectedVersionKind kind, long? value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>Gets the expectation kind.</summary>
    public ExpectedVersionKind Kind { get; }

    /// <summary>Gets the exact expected version, when applicable.</summary>
    public long? Value { get; }

    /// <summary>Creates an exact-version expectation.</summary>
    public static ExpectedVersion Exact(long version) =>
        version >= 0
            ? new(ExpectedVersionKind.Exact, version)
            : throw new ArgumentOutOfRangeException(nameof(version));

    /// <summary>Requires that the stream does not exist.</summary>
    public static ExpectedVersion NoStream { get; } = new(ExpectedVersionKind.NoStream, null);

    /// <summary>Requires that the stream already exists.</summary>
    public static ExpectedVersion StreamExists { get; } = new(ExpectedVersionKind.StreamExists, null);

    /// <summary>Allows any current stream version.</summary>
    public static ExpectedVersion Any { get; } = new(ExpectedVersionKind.Any, null);

    /// <summary>Determines whether a current version satisfies this expectation.</summary>
    internal bool IsMatch(long? currentVersion) => Kind switch
    {
        ExpectedVersionKind.Exact => currentVersion == Value,
        ExpectedVersionKind.NoStream => currentVersion is null,
        ExpectedVersionKind.StreamExists => currentVersion is not null,
        ExpectedVersionKind.Any => true,
        _ => false
    };
}
