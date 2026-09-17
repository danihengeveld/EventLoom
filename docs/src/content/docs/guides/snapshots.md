---
title: Snapshots
description: Capture versioned aggregate state to reduce replay while preserving event history as the source of truth.
---

Snapshots are an optional replay optimization. Event history remains the
authoritative source of truth: if a snapshot is missing, corrupt, or
incompatible, EventLoom recreates the aggregate and replays the full stream.

## Define a snapshot DTO and adapter

Use an immutable, versioned DTO with a stable name:

```csharp
[SnapshotType("orders.order", Version = 1)]
public sealed record OrderSnapshot(string Status, IReadOnlyList<OrderItem> Items)
    : IAggregateSnapshot;

var snapshots = new JsonAggregateSnapshotAdapter<Order, OrderSnapshot>(
    order => new OrderSnapshot(order.Status, order.Items.ToArray()),
    (order, snapshot) => order.Restore(snapshot));
```

The aggregate owns the `Restore` method so its snapshot state remains explicit
application behavior. Change the snapshot type version whenever its serialized
shape changes.

## Register a snapshot-enabled repository

Pass the adapter and a policy when registering the aggregate:

```csharp
eventLoom.AddAggregateRepository<Order, Guid>(
    id => new Order(id),
    aggregateType: "order",
    streamId: id => id.ToString("D"),
    snapshotAdapter: snapshots,
    snapshotPolicy: new EveryNEventsSnapshotPolicy(100));
```

If no policy is supplied, EventLoom captures a snapshot every 100 events. It
retains only the latest snapshot for each tenant and aggregate stream.

## Load and save normally

No call-site changes are needed:

```csharp
await repository.SaveAsync(order);
var reloaded = await repository.LoadAsync(order.Id);
```

On load, EventLoom restores the latest matching snapshot and replays events
after its stream version. Snapshot persistence occurs only after a successful
non-idempotent append. A snapshot write failure does not roll back or modify
committed event history.

Snapshots currently require matching schema versions. A snapshot with an
unknown version, malformed JSON, or incompatible type falls back to full
replay. Snapshot upcasters and custom retention policies are planned.
