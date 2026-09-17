---
title: Production-shaped ordering API
description: Run the EventLoom ordering sample with PostgreSQL or SQLite.
---

The repository includes [`samples/EventLoom.Ordering.Api`](https://github.com/danihengeveld/EventLoom/tree/main/samples/EventLoom.Ordering.Api), a small ASP.NET Core application that demonstrates the intended application boundary:

1. A command creates an aggregate and raises an immutable event.
2. `AggregateRepository` appends the event transactionally.
3. A query loads the stream and replays the event into a fresh aggregate.
4. PostgreSQL is the default distributed provider; SQLite is an explicit local alternative.

The sample uses one composition call rather than manually registering the
serializer, registry, clock, identifier generator, context, and event store:

```csharp
builder.Services.AddEventLoom(eventLoom =>
{
    eventLoom.RegisterEvent<OrderPlaced>();
    eventLoom.AddAggregateRepository<Order, Guid>(id => new Order(id));
    eventLoom.UsePostgreSql(connectionString);
});
```

Event registration is intentionally explicit. Assembly scanning and strict
source-generated serialization are available as advanced opt-ins, not hidden
startup conventions.

## Local SQLite

SQLite requires no server and is useful for development and single-node
deployments:

```bash
EVENTLOOM_DATABASE_PROVIDER=sqlite \
  dotnet run --project samples/EventLoom.Ordering.Api
```

## PostgreSQL

The sample includes a local PostgreSQL compose file:

```bash
docker compose -f samples/EventLoom.Ordering.Api/compose.yaml up -d
```

Then provide a normal EF Core connection string:

```bash
ConnectionStrings__EventStore='Host=localhost;Database=eventloom;Username=eventloom;Password=eventloom' \
  dotnet run --project samples/EventLoom.Ordering.Api
```

The application uses the `eventloom` schema and `eventloom_` table prefix for
PostgreSQL. For a real deployment, apply reviewed schema migrations before
starting the application rather than relying on startup schema creation.

## API walkthrough

Create an order:

```bash
curl -X POST http://localhost:5000/orders \
  -H 'content-type: application/json' \
  -d '{"sku":"coffee","quantity":2}'
```

Use the returned `id` to rebuild the order from its stream:

```bash
curl http://localhost:5000/orders/{id}
```

The sample intentionally keeps HTTP concerns outside the domain model. The
aggregate and event types are ordinary .NET types and can be reused by a
worker, command handler, or test.
