using EventLoom;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace EventLoom.Hosting;

/// <summary>Registers EventLoom instrumentation with OpenTelemetry SDK builders.</summary>
public static class EventLoomOpenTelemetryExtensions
{
    /// <summary>Adds EventLoom activities to an OpenTelemetry tracing pipeline.</summary>
    public static TracerProviderBuilder AddEventLoomInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(EventLoomTelemetry.ActivitySourceName);
    }

    /// <summary>Adds EventLoom metrics to an OpenTelemetry metrics pipeline.</summary>
    public static MeterProviderBuilder AddEventLoomInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(EventLoomTelemetry.MeterName);
    }
}
