---
title: Add observability
description: Opt into EventLoom OpenTelemetry tracing and metrics.
---

EventLoom emits standard .NET activities and metrics. They are inert unless an
application registers a listener. Event sourcing, snapshots, projections, and
outbox delivery work normally without any telemetry configuration.

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

The projection and outbox workers log lease loss at debug level and bounded
delivery failures at warning level. Failure records include only the
projection name/version or outbox message ID, retry attempt, and exception
type. They never add exception messages, payloads, headers, tenants, stream
IDs, or correlation identifiers as log properties. Configure log providers
with the same application-data safeguards.

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
