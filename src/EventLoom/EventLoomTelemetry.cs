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
    public static Meter Meter { get; } = new(MeterName);

    internal static Counter<long> Appends { get; } = Meter.CreateCounter<long>(
        "eventloom.appends",
        unit: "{append}",
        description: "The number of EventLoom append operations.");

    internal static Counter<long> AppendedEvents { get; } = Meter.CreateCounter<long>(
        "eventloom.appended.events",
        unit: "{event}",
        description: "The number of EventLoom events committed by append operations.");

    internal static Counter<long> AppendFailures { get; } = Meter.CreateCounter<long>(
        "eventloom.append.failures",
        unit: "{failure}",
        description: "The number of failed EventLoom append operations.");

    internal static Histogram<double> AppendDuration { get; } = Meter.CreateHistogram<double>(
        "eventloom.append.duration",
        unit: "ms",
        description: "The duration of EventLoom append operations.");

    internal static Counter<long> AggregateLoads { get; } = Meter.CreateCounter<long>(
        "eventloom.aggregate.loads",
        unit: "{load}",
        description: "The number of aggregate load operations.");

    internal static Counter<long> ReplayedEvents { get; } = Meter.CreateCounter<long>(
        "eventloom.replayed.events",
        unit: "{event}",
        description: "The number of events applied while loading aggregates.");

    internal static Histogram<double> AggregateLoadDuration { get; } = Meter.CreateHistogram<double>(
        "eventloom.aggregate.load.duration",
        unit: "ms",
        description: "The duration of aggregate load operations.");

    public static Counter<long> ProjectionDeliveries { get; } = Meter.CreateCounter<long>(
        "eventloom.projection.deliveries", unit: "{delivery}");

    public static Counter<long> ProjectionFailures { get; } = Meter.CreateCounter<long>(
        "eventloom.projection.failures", unit: "{failure}");

    public static Counter<long> OutboxDeliveries { get; } = Meter.CreateCounter<long>(
        "eventloom.outbox.deliveries", unit: "{delivery}");

    public static Counter<long> OutboxFailures { get; } = Meter.CreateCounter<long>(
        "eventloom.outbox.failures", unit: "{failure}");
}
