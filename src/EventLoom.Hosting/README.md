# EventLoom.Hosting

`EventLoom.Hosting` is EventLoom's public application-composition package. It
contains the `AddEventLoom` service-registration API, background projection and
outbox workers, health checks, and optional OpenTelemetry SDK extensions.

Install it alongside exactly one EventLoom provider package:

- `EventLoom.EntityFrameworkCore.PostgreSql` for the distributed production
  path;
- `EventLoom.EntityFrameworkCore.Sqlite` for local and controlled single-node
  use.

After configuring EventLoom, add readiness checks and optional telemetry with:

```csharp
using EventLoom.Hosting;

builder.Services.AddEventLoomHealthChecks();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddEventLoomInstrumentation())
    .WithMetrics(metrics => metrics.AddEventLoomInstrumentation());
```

Packages are currently pre-release and not published to NuGet. See the
[observability guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/observability.md)
and [production deployment guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/production-deployment.md)
for operational boundaries.
