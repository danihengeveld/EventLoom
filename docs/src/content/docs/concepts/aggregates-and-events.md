---
title: Aggregates and events
description: Model state changes as immutable domain events and replay them safely.
---

## Events are persisted contracts

An EventLoom event implements `IDomainEvent` and carries an `[EventType]`
attribute:

```csharp
[EventType("orders.order-placed", Version = 1)]
public sealed record OrderPlaced(string Sku, int Quantity) : IDomainEvent;
```

The name is a permanent storage contract. Choose a business-oriented,
lowercase, namespaced name; do not derive it from a namespace or CLR type.
Renaming `OrderPlaced` later does not require a migration when the stable event
name remains unchanged.

The event payload should not contain technical stream, tenant, or event
identity. EventLoom writes that information in the envelope.

## Aggregate lifecycle

```csharp
public sealed class Order(Guid id) : Aggregate<Guid>(id)
{
    public string Status { get; private set; } = "new";

    public void Place() => Raise(new OrderPlaced("coffee", 2));

    private void Apply(OrderPlaced @event) => Status = "placed";
}
```

`Raise` applies an event to the current aggregate and exposes it through
`PendingEvents`. `ApplyHistory` applies stored events without making them
pending. `AggregateRepository.SaveAsync` persists pending events and clears
them only after a non-idempotent successful append.

Use the aggregate's public behavior to enforce business invariants. Do not
change aggregate state outside `Apply` methods, or replay will produce a
different result from the live command path.

## Event registration

EventLoom does not scan assemblies by default:

```csharp
services.AddEventLoom(eventLoom => eventLoom
    .RegisterEvent<OrderPlaced>()
    .RegisterEvent<OrderCancelled>()
    .UsePostgreSql(connectionString));
```

Explicit registration makes the persisted event surface visible at startup and
fails early for missing or duplicate stable event names. Use
`RegisterEventsFromAssembly(assembly)` only when the application deliberately
accepts convention-based discovery.

## Strongly typed IDs

Aggregates can use any identifier type:

```csharp
public readonly record struct OrderId(Guid Value);

public sealed class Order(OrderId id) : Aggregate<OrderId>(id);
```

At the persistence boundary, configure one canonical stream conversion:

```csharp
eventLoom.AddAggregateRepository<Order, OrderId>(
    id => new Order(id),
    "order",
    id => id.Value.ToString("N"));
```

The conversion must be stable for the lifetime of the stream. EventLoom
includes `GuidIdConverter` and `StringIdConverter` for code that needs
bidirectional canonical conversion; the configured repository requires only
the forward stream-ID conversion.
