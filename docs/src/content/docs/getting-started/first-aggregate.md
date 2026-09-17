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

[EventType("counter.incremented", Version = 1)]
public sealed record CounterIncremented(int Amount) : IDomainEvent;

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
```

An aggregate changes state only by raising an event. `Raise` applies the event
immediately and stores it in `PendingEvents`; replay applies persisted history
without adding pending events. An `Apply` method must be private or protected,
take exactly one event type, and return `void`.

## Configure services

Register each event explicitly, configure repository identity once, and select
SQLite:

```csharp
services.AddScoped<ITenantAccessor>(_ => new FixedTenantAccessor("demo"));
services.AddEventLoom(eventLoom => eventLoom
    .ConfigureTenancy(TenancyMode.Required)
    .RegisterEvent<CounterIncremented>()
    .AddAggregateRepository<Counter, Guid>(
        id => new Counter(id),
        "counter",
        id => id.ToString("D"))
    .UseSqlite("Data Source=eventloom.db"));
```

`FixedTenantAccessor` is appropriate only for a single-tenant application. In
an HTTP service, resolve the tenant from validated authentication or request
context; see [Tenancy and ordering](/concepts/tenancy-and-ordering).

## Create the schema for local development

For a local prototype, create the EventLoom schema once:

```csharp
await using var scope = services.BuildServiceProvider().CreateAsyncScope();
var context = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
await context.Database.EnsureCreatedAsync();
```

`EnsureCreatedAsync` is convenient for the current pre-release and sample. Do
not use it for a database that will be managed with EF Core migrations. See
[Production deployment](/guides/production-deployment) for the current schema
deployment boundary.

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

```csharp
internal sealed class FixedTenantAccessor(string tenant) : ITenantAccessor
{
    public TenantId? TenantId { get; } = new TenantId(tenant);
}
```
