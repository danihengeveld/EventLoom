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

var snapshots = new AggregateSnapshotAdapter<Order, OrderSnapshot>(
    order => new OrderSnapshot(order.Status, order.Items.ToArray()),
    (order, snapshot) => order.Restore(snapshot));
```

The aggregate owns the `Restore` method so its snapshot state remains explicit
application behavior. Change the snapshot type version whenever its serialized
shape changes.

## Register a snapshot-enabled aggregate

Keep all snapshot concerns together in the aggregate registration:

```csharp
eventLoom.AddAggregate<Order, Guid>(aggregate => aggregate
    .ConstructWith(id => new Order(id))
    .UseStream("order", id => id.ToString("D"))
    .UseSnapshots(snapshot => snapshot
        .UseAdapter(
            order => new OrderSnapshot(order.Status, order.Items.ToArray()),
            (order, value) => order.Restore(value))
        .Every(100)
        .KeepLatest(3)
        .UseInvalidator(new RemoveUnusableOrderSnapshots())));
```

The typed builder supports an existing adapter as well as the DTO adapter
factory. Use `UsePolicy(...)` for custom cadence logic, `UseRetention(...)` for
custom retention, and provide `upcasters` to `UseAdapter(...)` when a snapshot
schema needs deterministic version-by-version migration.

If no policy is supplied, EventLoom captures a snapshot every 100 events. It
retains only the latest snapshot for each tenant and aggregate stream.

Retention can be configured for one aggregate in the typed builder. Configure
it globally when all aggregates should share the same default:

```csharp
eventLoom.ConfigureSnapshotRetention(new KeepLatestSnapshotsPolicy(3));
```

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

A snapshot at the adapter's current schema version is deserialized directly.
A malformed payload, unsupported future version, or incompatible type falls
back to full replay. Pass deterministic `ISnapshotUpcaster` implementations
to `AggregateSnapshotAdapter` when upgrading a snapshot DTO one schema version
at a time:

```csharp
public sealed class OrderSnapshotV1ToV2 : ISnapshotUpcaster
{
    public string SnapshotType => "orders.order";
    public int FromVersion => 1;
    public int ToVersion => 2;

    public JsonElement Upcast(JsonElement payload) =>
        JsonSerializer.SerializeToElement(new
        {
            status = payload.GetProperty("status").GetString(),
            items = payload.GetProperty("items"),
            currency = "EUR"
        });
}

var snapshots = new AggregateSnapshotAdapter<Order, OrderSnapshot>(
    order => new OrderSnapshot(order.Status, order.Items.ToArray(), "EUR"),
    (order, snapshot) => order.Restore(snapshot),
    upcasters: [new OrderSnapshotV1ToV2()]);
```

Every upcaster advances exactly one schema version and chains must be complete
and unambiguous. EventLoom otherwise ignores the snapshot and replays the full
stream. To remove unusable snapshots after that safe fallback, opt in with an
`ISnapshotInvalidator`; without one, EventLoom leaves the snapshot untouched
for diagnosis.

```csharp
eventLoom.AddAggregate<Order, Guid>(aggregate => aggregate
    .ConstructWith(id => new Order(id))
    .UseStream("order", id => id.ToString("D"))
    .UseSnapshots(snapshot => snapshot
        .UseAdapter(snapshots)
        .UseInvalidator(new RemoveUnusableOrderSnapshots())));
```
