# EventLoom

`EventLoom` is the core package for immutable, versioned domain events and
event-sourced aggregates. It contains event contracts, stable
`[EventType]` names, strongly typed identifiers, metadata, expected-version
rules, aggregate dispatch, and serializer registration.

Install it directly when your domain layer needs EventLoom abstractions:

```bash
dotnet add package EventLoom --prerelease
```

Define a stable persisted event and apply it through an aggregate:

```csharp
using EventLoom;

[EventType("inventory.received", Version = 1)]
public sealed record InventoryReceived(int Quantity) : IDomainEvent;

public sealed class InventoryItem(Guid id) : Aggregate<Guid>(id)
{
    public int Available { get; private set; }

    public void Receive(int quantity) => Raise(new InventoryReceived(quantity));

    private void Apply(InventoryReceived @event) => Available += @event.Quantity;
}
```

This package has no application composition or storage provider. Web
applications should add `EventLoom.AspNetCore` plus exactly one provider:
`EventLoom.EntityFrameworkCore.PostgreSql` for distributed production or
`EventLoom.EntityFrameworkCore.Sqlite` for local and single-node use.

Packages are currently pre-release and not published to NuGet. See the
[getting started guide](https://github.com/danihengeveld/EventLoom/tree/main/docs/src/content/docs/getting-started)
and [architecture documentation](https://github.com/danihengeveld/EventLoom/tree/main/docs/architecture).
