---
title: Build your first aggregate
description: Define immutable events, configure EventLoom, and save and reload an aggregate.
---

This guide creates a small counter aggregate using SQLite. It uses the same
aggregate and repository APIs that a PostgreSQL application uses.

## Define the event and aggregate

Events are immutable application-owned values. Give every persisted event a
stable name that is independent of its CLR type and a positive schema version.

```csharp
using EventLoom;

public sealed class Counter(Guid id) : Aggregate<Guid>(id)
{
    public int Value { get; private set; }

    public void Increment(int amount)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        Raise(new CounterIncremented(amount));
    }

    private void Apply(CounterIncremented @event) => Value += @event.Amount;
}

[EventType("counter.incremented", Version = 1)]
public sealed record CounterIncremented(int Amount) : IDomainEvent<Counter>;
```

The generic event contract links the event to its owning aggregate. EventLoom's
analyzer verifies that `Counter` has exactly one correctly shaped
`Apply(CounterIncremented)` method and that another aggregate cannot raise this
event. An aggregate changes state only by raising an event. `Raise` applies the event
immediately and stores it in `PendingEvents`; replay applies persisted history
without adding pending events. An `Apply` method must be private or protected,
take exactly one event type, and return `void`.

## Configure services

Register each event explicitly, configure repository identity once, and select
SQLite:

```csharp
services
    .AddEventLoom()
    .UseSqlite("Data Source=eventloom.db")
    .AddEvent<CounterIncremented>()
    .AddAggregate<Counter, Guid>(aggregate => aggregate
        .ConstructWith(id => new Counter(id))
        .UseStream("counter", id => id.ToString("D")));
```

Single-tenancy is the default and uses the stable internal tenant ID `default`.
Applications that need tenant isolation opt in with
`UseMultiTenancy<TAccessor>()`; see
[Tenancy and ordering](/concepts/tenancy-and-ordering).

## Create the schema for local development

For a local ASP.NET Core prototype, explicitly initialize a new database:

```csharp
if (app.Environment.IsDevelopment())
{
    await app.InitializeEventLoomDevelopmentDatabaseAsync();
}
```

The helper refuses to run outside Development and does not migrate an existing
schema. Use reviewed, host-owned EF Core migrations in production. See
[Production deployment](/guides/production-deployment).

## Save and reload

The configured repository resolves the scoped tenant and derives the stream ID
and aggregate type:

```csharp
var counter = new Counter(Guid.NewGuid());
counter.Increment(3);

await repository.SaveAsync(
    counter,
    new EventMetadata(Actor: "counter-api"),
    appendId: "increment-command-42");

var reloaded = await repository.LoadAsync(counter.Id);
Console.WriteLine(reloaded.Value); // 3
```

`appendId` is optional. Supply a stable caller-owned command identifier when
retrying after an ambiguous transport failure; do not generate a new value for
each retry. See [Append and read events](/guides/append-and-read) for expected
versions, metadata, and explicit administrative operations.
