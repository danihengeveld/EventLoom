# EventLoom.Hosting

`EventLoom.Hosting` contains EventLoom's host-neutral service composition,
background projection and outbox workers, health checks, and optional
OpenTelemetry SDK extensions.

ASP.NET Core applications should install `EventLoom.AspNetCore` plus exactly
one provider package. Reference `EventLoom.Hosting` directly only for a
non-web host:

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

Register each durable projection identity once and select its delivery behavior
per handler:

```csharp
eventLoom.AddProjection("orders.summary", projection => projection
    .Transactional<OrderSummaryProjection, OrderPlaced>()
    .Asynchronous<OrderNotificationsProjection, OrderPlaced>());
```

`Asynchronous` is at-least-once, `Transactional` commits EF read-model changes
with its checkpoint, and `Inline` runs within the event append transaction.
