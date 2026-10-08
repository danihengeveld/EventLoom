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
Control verbosity with the host's normal logging configuration, for example:

```csharp
builder.Logging.AddFilter("EventLoom", LogLevel.Warning);
```

The `EventLoom.*` logger categories include the provider-neutral engine,
storage-provider implementations, and hosted workers. Warnings report a
projection paused after persisted failures, an outbox delivery cycle that
exhausted retries, and snapshot fallback to event history. Debug logs describe
transient provider retries, intermediate worker delivery retries, lease loss,
and rejected appends. Successful projection resume, skip, and replay operations
log once at information level. Ordinary appends, reads, successful deliveries,
and empty worker polls do not log.

`EventLoom.Hosting` includes optional convenience extensions for applications
using the OpenTelemetry SDK:

```csharp
using EventLoom.Hosting;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddEventLoomInstrumentation())
    .WithMetrics(metrics => metrics.AddEventLoomInstrumentation());
```

## Traces

The current instrumentation includes:

- `eventloom.append` for an event append;
- `eventloom.aggregate.load` for aggregate rehydration;
- `eventloom.snapshot.read` when a configured repository looks up a snapshot;
- `eventloom.event-stream.read` for the history or tail query;
- `eventloom.aggregate.replay` when tail events are applied.

Aggregate-load spans form the parent trace for snapshot lookup, tail reads, and
replay.

## Metrics

The `EventLoom` meter currently provides:

- `eventloom.appends`;
- `eventloom.appended.events`;
- `eventloom.append.failures`;
- `eventloom.append.duration` in milliseconds;
- `eventloom.aggregate.loads`;
- `eventloom.replayed.events`;
- `eventloom.aggregate.load.duration` in milliseconds;
- `eventloom.projection.deliveries` and `eventloom.projection.failures`;
- `eventloom.projection.lease_losses`;
- `eventloom.outbox.deliveries`, `eventloom.outbox.failures`, and
  `eventloom.outbox.lease_losses`.

## Data safety

EventLoom records stable operation metadata such as aggregate type, event
counts, snapshot use, and replay-tail count. It intentionally excludes event
payloads, stream IDs, tenant IDs, event IDs, correlation and causation IDs, and
application headers from default span and metric attributes.

Default EventLoom logs include only stable operation metadata. They do not
contain exception objects or messages, event payloads, headers, tenant IDs,
stream IDs, event or outbox message IDs, or correlation identifiers. Persisted
projection failures and outbox attempt histories remain available through their
authorized tenant-scoped administration APIs.

## Health checks

After configuring EventLoom, register its readiness checks and expose the
endpoint from an ASP.NET Core host:

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
builder.Services.AddEventLoomHealthChecks(options =>
{
    options.MaximumProjectionLag = 500;
    options.MaximumOutboxBacklog = 500;
});

var app = builder.Build();
app.MapEventLoomHealthChecks();
```

The `eventloom.event-store` check verifies connectivity and runs the read-only
`IStorageSchema.ValidateAsync()` compatibility check. It reports
`missing_object_count` and `incompatible_object_count` when the store is
reachable but incompatible.

`eventloom.projections` is unhealthy for unresolved projection failures and
degraded when event-offset lag exceeds `MaximumProjectionLag`.
`eventloom.outbox` is degraded when unpublished message count exceeds
`MaximumOutboxBacklog`.

## Protected diagnostics endpoints

`MapEventLoomAdminDiagnostics("EventLoomOperators")` maps protected, aggregate-only
diagnostics:

- `GET /admin/eventloom/schema` returns `IsCompatible`, `CanConnect`,
  `MissingCount`, and `IncompatibleCount`;
- `GET /admin/eventloom/diagnostics` returns the same schema data plus
  projection and outbox backlog summaries.

Resolve `IStorageSchema` directly in deployment tooling when an explicit storage
gate is needed. Validation is read-only: it neither creates nor migrates a
store.
