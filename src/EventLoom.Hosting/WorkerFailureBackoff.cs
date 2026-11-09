namespace EventLoom.Hosting;

/// <summary>Computes the delay a background worker waits after a failed iteration.</summary>
internal static class WorkerFailureBackoff
{
    /// <summary>The longest delay between iterations while failures persist, unless the poll interval is longer.</summary>
    internal static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);

    private const int MaximumExponent = 16;

    /// <summary>
    /// Doubles the poll interval for each consecutive failure, capped at <see cref="MaximumDelay"/>
    /// or the poll interval, whichever is longer.
    /// </summary>
    internal static TimeSpan GetDelay(TimeSpan pollInterval, int consecutiveFailures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(consecutiveFailures, 1);

        var ceiling = pollInterval > MaximumDelay ? pollInterval : MaximumDelay;
        var exponent = Math.Min(consecutiveFailures - 1, MaximumExponent);
        var ticks = pollInterval.Ticks * Math.Pow(2, exponent);
        return ticks >= ceiling.Ticks ? ceiling : TimeSpan.FromTicks((long)ticks);
    }
}
