---
title: Append and read events
description: Persist event batches, rebuild aggregates, and consume a tenant's committed event order.
---

There are two EventLoom application paths:

1. Use `AggregateRepository<TAggregate, TId>` for ordinary command handlers.
2. Use `EventStore` when a process needs explicit stream identity or consumes
   position-ordered envelopes.

Both paths require events to be registered and a provider to be configured;
see [Configure the event store](/guides/configure-ef-core).

## Save and load an aggregate

Configure its persistence identity once:

```csharp
eventLoom.AddAggregateRepository<Order, Guid>(
    id => new Order(id),
    "order",
    id => id.ToString("D"));
```

With a scoped tenant accessor, command code stays focused on domain behavior:

```csharp
var order = new Order(orderId);
order.Place("coffee", 2);

var result = await repository.SaveAsync(
    order,
    new EventMetadata(
        CorrelationId: correlationId,
        Actor: currentUserId),
    appendId: commandId,
    cancellationToken);

var reloaded = await repository.LoadAsync(orderId, cancellationToken);
```

`SaveAsync` returns an empty result when no events are pending. On a normal
successful append it clears pending events; an idempotent replay retains the
aggregate's pending events because the caller may need to resolve the
ambiguous-command outcome explicitly.

`EventMetadata` stores correlation ID, causation ID, and actor in nullable
columns. Application headers are serialized only when nonempty; an empty header
collection is stored as `NULL` and is rehydrated as an empty read-only
dictionary.

## Use the explicit store API

Use `EventStore` when a background, import, repair, or integration workflow
must provide explicit identity:

```csharp
var result = await store.AppendAsync(new AppendRequest(
    TenantId: "acme",
    StreamId: "order-42",
    AggregateType: "order",
    ExpectedVersion: ExpectedVersion.Exact(3),
    Events: [new OrderCancelled("duplicate")],
    Metadata: new EventMetadata(Actor: "support-tool"),
    AppendId: "support-command-123"),
    cancellationToken);
```

The event batch is committed atomically. Versions begin at 1, and all events
in a batch receive consecutive stream versions and tenant offsets.

## Read one stream

```csharp
var history = await store.ReadStreamAsync(
    tenantId: "acme",
    streamId: "order-42",
    fromVersion: 2,
    toVersion: 5,
    cancellationToken: cancellationToken);
```

`ReadStreamAsync` returns persisted envelopes in stream-version order. Passing
neither bound reads the full stream. Bounds must be positive and ordered.

## Read a tenant's committed order

For a consumer that maintains a checkpoint:

```csharp
var batch = await store.ReadTenantOffsetsAsync(
    tenantId: "acme",
    afterPosition: checkpoint,
    limit: 100,
    cancellationToken: cancellationToken);

foreach (var envelope in batch)
{
    // Dispatch to application-owned handling code.
    checkpoint = envelope.TenantOffset;
}
```

Tenant offsets are authoritative only inside one tenant. Keep checkpoints
tenant-scoped, process in order, and make handlers idempotent. Projection
runner infrastructure is not yet included in EventLoom.

## Handle failures correctly

- `WrongExpectedVersionException` means the stream changed relative to the
  request's expectation. Reload and reevaluate the business command.
- `EventStoreConcurrencyException` means the database reported a concurrent
  append conflict. Treat it like an optimistic-concurrency failure.
- PostgreSQL retries classified transient, deadlock, and serialization failures
  a bounded number of times. It never silently retries logical conflicts.
- Reuse the same caller-owned `AppendId` after an ambiguous failure. Do not
  reuse an append ID for a different command in the same tenant.

See [Tenancy and ordering](/concepts/tenancy-and-ordering) for the full
concurrency and retry model.
