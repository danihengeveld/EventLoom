# EventLoom

`EventLoom` is the core package for immutable, versioned domain events and
event-sourced aggregates. It contains compiler-checked event contracts, stable
persisted event names, strongly typed identifiers, metadata, expected-version
rules, aggregate dispatch, snapshot contracts, JSON serialization settings,
and bundled analyzer diagnostics.

Install it directly when your domain layer needs EventLoom abstractions:

```bash
dotnet add package EventLoom --prerelease
```

Define a stable persisted event and apply it through an aggregate:

```csharp
using EventLoom;

public sealed record InventoryReceived(int Quantity)
    : IDomainEvent<InventoryReceived, InventoryItem>
{
    public static string EventType => "inventory.received";
}

public sealed class InventoryItem(Guid id) : Aggregate<InventoryItem, Guid>(id),
    IApply<InventoryReceived>
{
    public int Available { get; private set; }

    public void Receive(int quantity) => Raise(new InventoryReceived(quantity));

    void IApply<InventoryReceived>.Apply(InventoryReceived @event) =>
        Available += @event.Quantity;
}
```

`EventVersion` is optional and defaults to `1`. Increase it only with a
registered upcaster for the same stable `EventType` string.

This package has no application composition or storage provider. Web
applications should add `EventLoom.AspNetCore` plus exactly one provider:
`EventLoom.EntityFrameworkCore.PostgreSql` or `EventLoom.MongoDb` for
distributed production, or `EventLoom.EntityFrameworkCore.Sqlite` for local and
single-node use.

EventLoom is currently pre-release. See the
[getting started guide](https://github.com/danihengeveld/EventLoom/tree/main/docs/src/content/docs/getting-started)
and [core concepts](https://github.com/danihengeveld/EventLoom/tree/main/docs/src/content/docs/concepts).
