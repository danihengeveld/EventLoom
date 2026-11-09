using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EventLoom;

/// <summary>Provides EventLoom's stable OpenTelemetry activity and metric instruments.</summary>
public static class EventLoomTelemetry
{
    /// <summary>The activity source name used by EventLoom instrumentation.</summary>
    public const string ActivitySourceName = "EventLoom";

    /// <summary>The meter name used by EventLoom instrumentation.</summary>
    public const string MeterName = "EventLoom";

    /// <summary>Gets the EventLoom activity source.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    /// <summary>Gets the EventLoom metrics meter.</summary>
    /// <remarks>
    /// Each EventLoom package creates the instruments it records on this shared meter, so every EventLoom metric is
    /// published under <see cref="MeterName"/>.
    /// </remarks>
    public static Meter Meter { get; } = new(MeterName);
}
