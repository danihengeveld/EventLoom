---
title: Test an EventLoom application
description: Test event-sourced domain behavior separately from provider contracts, storage behavior, and distributed-worker behavior.
---

## Test domain behavior without a database

Most application tests should create an aggregate, invoke one command, and
assert the resulting pending events and state. `EventLoom.Testing` provides
`AggregateScenario<TAggregate, TId>` for a Given/When/Then shape:

```csharp
using EventLoom.Testing;

var scenario = AggregateScenario.For<Order, Guid>(id => new Order(id))
    .Given(orderId) // no prior history: this is a new order
    .When(order => order.Place("coffee", 2));

await Assert.That(scenario.Aggregate.Status).IsEqualTo("placed");
await Assert.That(scenario.RaisedEvents.Single()).IsTypeOf<OrderPlaced>();
```

`Given` replays prior events as already-persisted history (without adding them
to pending events), so a scenario can also start from an existing aggregate:

```csharp
AggregateScenario.For<Order, Guid>(id => new Order(id))
    .Given(orderId, new OrderPlaced("coffee", 2))
    .When(order => order.Cancel("out of stock"))
    .ThenEvents(events => Assert.That(events.Single()).IsTypeOf<OrderCancelled>())
    .Then(order => Assert.That(order.Status).IsEqualTo("cancelled"));
```

`ThenNoEventsRaised()` asserts an idempotent no-op command, and
`ThenThrows<TException>()` asserts that a command was rejected.

Keep tests for aggregate invariants, event payloads, registration,
serialization, and upcasters independent of storage providers.

## Test provider behavior with real stores

Use real providers for append, rollback, unique-index, lease, and query
behavior:

- `EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests` uses in-memory
  SQLite for local relational behavior.
- `EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests` uses PostgreSQL
  Testcontainers for distributed relational behavior.
- `EventLoom.MongoDb.IntegrationTests` uses MongoDB Testcontainers or an
  explicit `EVENTLOOM_MONGODB_CONNECTION_STRING` for distributed document-store
  behavior.
- `tests/EventLoom.Storage.Conformance` is a shared provider-neutral suite that
  each integration project runs against its provider implementation.

SQLite is useful for local integration tests, but it cannot prove PostgreSQL or
MongoDB multi-instance behavior.

`EventLoomSqliteTestHost` (in `EventLoom.Testing`) wraps the SQLite setup: a
kept-open in-memory connection, a fully wired EventLoom container, schema
creation, deterministic time (`ManualTimeProvider`), and deterministic event
identifiers.

```csharp
await using var host = await EventLoomSqliteTestHost.CreateAsync(options =>
    options.ConfigureEventLoom = builder => builder
        .AddAggregate<Order, Guid>(aggregate => aggregate
            .ConstructWith(id => new Order(id))
            .UseStream("order", id => id.ToString("D"))));

await host.RunScopedAsync(async (services, cancellationToken) =>
{
    var repository = services.GetRequiredService<AggregateRepository<Order, Guid>>();
    var order = new Order(Guid.NewGuid());
    order.Place("coffee", 2);
    await repository.SaveAsync(order, cancellationToken: cancellationToken);
});
```

## Test distributed providers deliberately

### PostgreSQL

Use Testcontainers for PostgreSQL integration tests that claim distributed
correctness. Create independent contexts for simulated application instances and
test competing appends, first-write races, per-tenant offsets, and stale lease
fencing.

### MongoDB

Use a replica set, not a standalone `mongod`. The repository's MongoDB provider
supports Testcontainers or an explicit local server through
`EVENTLOOM_MONGODB_CONNECTION_STRING`. Validate transactional projections,
shared unit-of-work sessions, tenant offsets, idempotent append replay, and
lease fencing.

For local developer setup, see [Use MongoDB](/guides/use-mongodb).

## What to assert

| Behavior | Assert |
| --- | --- |
| Aggregate command | Event type, payload, state, and pending event count. |
| Replay | Same state after loading persisted history. |
| Append | All events appear once with consecutive stream versions and tenant offsets. |
| Concurrency | One command succeeds; the other is a visible conflict or reevaluated retry. |
| Tenant access | A tenant never reads another tenant's stream or offsets. |
| Idempotency | The same append ID returns the original envelopes across the whole tenant scope. |
| Projection | Checkpoint advances only after a handler succeeds; transactional projection effects commit with it. |
| Lease fencing | Stale workers cannot record checkpoint or outbox progress. |
| Lease release | Releasing a lease expires it without resetting fencing-token monotonicity. |
| Outbox | Event and message commit together; retries reuse the stable message ID and persist attempt history. |

Run the provider-neutral conformance suite plus the smallest provider-specific
tests that prove capabilities or operational differences your application relies
on.
