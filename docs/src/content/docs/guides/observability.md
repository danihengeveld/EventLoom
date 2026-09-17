---
title: Observability
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

## Data safety

EventLoom records stable operation metadata such as aggregate type, event
counts, snapshot use, and replay-tail count. It intentionally excludes event
payloads, stream IDs, tenant IDs, event IDs, correlation and causation IDs,
and application headers from default span and metric attributes. Add
application-specific enrichment only after evaluating its cardinality and
sensitivity.
