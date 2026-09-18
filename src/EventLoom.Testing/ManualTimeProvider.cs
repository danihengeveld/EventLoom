namespace EventLoom.Testing;

/// <summary>A settable <see cref="TimeProvider"/> for deterministic time in tests.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset utcNow;

    /// <summary>Initializes a manual time provider starting at a fixed instant.</summary>
    /// <param name="start">
    /// The initial current time. Defaults to <c>2000-01-01T00:00:00Z</c> when omitted.
    /// </param>
    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        utcNow = start ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() => utcNow;

    /// <summary>Sets the current time returned by <see cref="GetUtcNow"/>.</summary>
    /// <param name="value">The new current time.</param>
    public void SetUtcNow(DateTimeOffset value) => utcNow = value;

    /// <summary>Moves the current time forward by a duration.</summary>
    /// <param name="duration">The non-negative duration to advance by.</param>
    /// <returns>The new current time.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    public DateTimeOffset Advance(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        utcNow += duration;
        return utcNow;
    }
}
