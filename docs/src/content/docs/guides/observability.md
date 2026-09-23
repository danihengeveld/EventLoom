---
title: Add observability
description: Use default .NET logging and optionally add OpenTelemetry tracing and metrics.
---

EventLoom emits standard .NET activities and metrics. They are inert unless an
application registers a listener. Event sourcing, snapshots, projections, and
outbox delivery work normally without any telemetry configuration.

## Logs

EventLoom uses `Microsoft.Extensions.Logging` by default. `AddEventLoom()`
registers the logging services; no EventLoom logging opt-in or OpenTelemetry
integration is needed. Logs go to the providers configured by the application.
ASP.NET Core and generic hosts normally configure providers already; an
application that builds a bare service collection must add its own provider
if it wants logs to go anywhere. Control verbosity with the host's normal
logging configuration, for example:

```csharp
builder.Logging.AddFilter("EventLoom", LogLevel.Warning);
```

The `EventLoom.*` logger categories include the EF Core store, PostgreSQL
retry policy, and hosted workers. Warnings report a projection paused after
persisted failures, an outbox delivery cycle that exhausted retries, and
snapshot fallback to event history. An outbox message remains pending after
its delivery cycle fails. An unexpected append failure logs at error level.
Debug logs describe transient PostgreSQL retries, intermediate worker
delivery retries, lease loss, and rejected appends. Successful projection
resume, skip, and replay operations log once at information level. Ordinary
appends, reads, successful deliveries, and empty worker polls do not log.
Health checks and operational summaries remain the way to inspect current
lag and backlog.

`EventLoom.Hosting` includes optional convenience extensions for applications
using the OpenTelemetry SDK:

```csharp
using EventLoom.Hosting;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddEventLoomInstrumentation())
    .WithMetrics(metrics => metrics.AddEventLoomInstrumentation());
```

`AddEventLoomInstrumentation()` registers both the `EventLoom` activity source
and meter with the SDK builder. Configure exporters such as OTLP, Prometheus,
or Application Insights separately according to your application’s
observability platform.

## Traces

The current instrumentation includes:

- `eventloom.append` for an event append;
- `eventloom.aggregate.load` for aggregate rehydration;
- `eventloom.snapshot.read` when a configured repository looks up a snapshot;
- `eventloom.event-stream.read` for the history or tail query;
- `eventloom.aggregate.replay` when tail events are applied.

Aggregate-load spans form the parent trace for snapshot lookup, tail reads, and
replay. This shows whether load latency comes from the snapshot, database read,
or applying a long tail.

## Metrics

The `EventLoom` meter currently provides:

- `eventloom.appends`;
- `eventloom.appended.events`;
- `eventloom.append.failures`;
- `eventloom.append.duration` in milliseconds;
- `eventloom.aggregate.loads`;
- `eventloom.replayed.events`;
- `eventloom.aggregate.load.duration` in milliseconds.
- `eventloom.projection.deliveries` and `eventloom.projection.failures`;
- `eventloom.projection.lease_losses`;
- `eventloom.outbox.deliveries`, `eventloom.outbox.failures`, and
  `eventloom.outbox.lease_losses`.

## Data safety

EventLoom records stable operation metadata such as aggregate type, event
counts, snapshot use, and replay-tail count. It intentionally excludes event
payloads, stream IDs, tenant IDs, event IDs, correlation and causation IDs,
and application headers from default span and metric attributes. Add
application-specific enrichment only after evaluating its cardinality and
sensitivity.

Default EventLoom logs include only stable operation metadata (such as
projection or aggregate type, attempt count, snapshot schema, and exception
type). They do not contain exception objects or messages, event payloads,
headers, tenant IDs, stream IDs, event or outbox message IDs, or correlation
identifiers. Persisted projection failures and outbox attempt histories
remain available through their authorized tenant-scoped administration APIs.
Projection skip and replay logs are not a substitute for an application audit
record with an authenticated actor and authorized tenant. Configure logging
providers with the same application-data safeguards; EF Core's sensitive-data
logging is separately controlled by the application.

## Health checks

After configuring EventLoom, register its readiness checks and expose the
endpoint from an ASP.NET Core host:

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddEvent<OrderPlaced>();
builder.Services.AddEventLoomHealthChecks(options =>
{
    options.MaximumProjectionLag = 500;
    options.MaximumOutboxBacklog = 500;
});

var app = builder.Build();
app.MapEventLoomHealthChecks();
```

The `eventloom.event-store` check verifies database connectivity and runs the
read-only `EventStoreSchema.ValidateAsync` compatibility check. It reports
counts of missing or incompatible EventLoom tables and mapped columns without
application event data. The ASP.NET Core `MapEventLoomHealthChecks` convention
returns 503 for both degraded and unhealthy EventLoom readiness, preventing a
lagging or backed-up instance from being selected as ready.
`eventloom.projections` is unhealthy for unresolved projection failures and
degraded when event-offset lag exceeds `MaximumProjectionLag`.
Its lag uses each tenant's persisted highest offset minus the corresponding
projection checkpoint (or zero before its first delivery); tenants without
committed events contribute no lag. No event payload or event-row scan is
needed for this summary.
`eventloom.outbox` is degraded when unpublished message count exceeds
`MaximumOutboxBacklog`. Diagnostics contain only aggregate counts and offsets,
never payloads, tenant IDs, stream IDs, event IDs, or headers.

Call `EventStoreSchema.ValidateAsync(context)` directly in deployment tooling
when an explicit schema gate is needed. It is validation only: it neither
creates a database nor applies migrations.
