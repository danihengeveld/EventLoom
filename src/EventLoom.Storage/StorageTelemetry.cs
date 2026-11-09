using System.Diagnostics.Metrics;

namespace EventLoom.Storage;

/// <summary>The append and aggregate-load instruments recorded by the storage engine.</summary>
internal static class StorageTelemetry
{
    internal static Counter<long> Appends { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.appends",
        unit: "{append}",
        description: "The number of EventLoom append operations.");

    internal static Counter<long> AppendedEvents { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.appended.events",
        unit: "{event}",
        description: "The number of EventLoom events committed by append operations.");

    internal static Counter<long> AppendFailures { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.append.failures",
        unit: "{failure}",
        description: "The number of failed EventLoom append operations.");

    internal static Histogram<double> AppendDuration { get; } = EventLoomTelemetry.Meter.CreateHistogram<double>(
        "eventloom.append.duration",
        unit: "ms",
        description: "The duration of EventLoom append operations.");

    internal static Counter<long> AggregateLoads { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.aggregate.loads",
        unit: "{load}",
        description: "The number of aggregate load operations.");

    internal static Counter<long> ReplayedEvents { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.replayed.events",
        unit: "{event}",
        description: "The number of events applied while loading aggregates.");

    internal static Histogram<double> AggregateLoadDuration { get; } =
        EventLoomTelemetry.Meter.CreateHistogram<double>(
            "eventloom.aggregate.load.duration",
            unit: "ms",
            description: "The duration of aggregate load operations.");
}
