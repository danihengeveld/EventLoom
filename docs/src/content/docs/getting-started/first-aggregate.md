---
title: Build your first aggregate
description: Define immutable events, configure EventLoom, and save and reload an aggregate.
---

This guide creates a small counter aggregate using SQLite. It uses the same
aggregate and repository APIs that a PostgreSQL application uses.

## Define the event and aggregate

Events are immutable application-owned values. Give every persisted event a
stable name that is independent of its CLR type and a positive schema version.
Declare the event with its owning aggregate; the compiler then requires that
the aggregate can apply it.

```csharp
using EventLoom;

public sealed record CounterIncremented(int Amount)
    : IDomainEvent<CounterIncremented, Counter>
{
    public static string EventType => "counter.incremented";
}

public sealed class Counter(Guid id) : Aggregate<Counter, Guid>(id),
    IApply<CounterIncremented>
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

    void IApply<CounterIncremented>.Apply(CounterIncremented @event) =>
        Value += @event.Amount;
}
```

`EventVersion` is optional and defaults to `1`; add
`public static int EventVersion => 2;` only when evolving the persisted JSON
with an upcaster. `Raise` only accepts events owned by the aggregate, so another
aggregate cannot raise `CounterIncremented`. An event whose aggregate lacks the
matching `IApply<TEvent>` handler fails to compile.

An aggregate changes state only by raising an event. `Raise` applies the event
immediately and stores it in `PendingEvents`; replay applies persisted history
without adding pending events. Implement handlers explicitly so application
code cannot call them directly.

## Configure services

Register repository identity once and select SQLite. Aggregate registration
automatically registers the events owned through `IApply<TEvent>`:

```csharp
services
    .AddEventLoom()
    .UseSqlite("Data Source=eventloom.db")
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
