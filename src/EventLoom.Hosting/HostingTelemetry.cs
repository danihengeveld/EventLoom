using System.Diagnostics.Metrics;

namespace EventLoom.Hosting;

/// <summary>The projection and outbox worker instruments recorded by the hosted workers.</summary>
internal static class HostingTelemetry
{
    internal static Counter<long> ProjectionDeliveries { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.projection.deliveries", unit: "{delivery}");

    internal static Counter<long> ProjectionFailures { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.projection.failures", unit: "{failure}");

    internal static Counter<long> ProjectionLeaseLosses { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.projection.lease_losses", unit: "{loss}");

    internal static Counter<long> OutboxDeliveries { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.outbox.deliveries", unit: "{delivery}");

    internal static Counter<long> OutboxFailures { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.outbox.failures", unit: "{failure}");

    internal static Counter<long> OutboxLeaseLosses { get; } = EventLoomTelemetry.Meter.CreateCounter<long>(
        "eventloom.outbox.lease_losses", unit: "{loss}");
}
