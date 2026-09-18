---
title: Test an EventLoom application
description: Test event-sourced domain behavior separately from relational and distributed storage behavior.
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

`EventLoom.Testing` also includes `EventTestBuilder<TEvent>` for concise event
fixtures. Keep tests for aggregate invariants, event payloads, registration,
serialization, and upcasters independent of EF Core.

## Test relational behavior with real providers

Use a real relational provider for append, rollback, unique-index, and query
behavior:

- `EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests` uses in-memory
  SQLite for schema, append/read, and aggregate repository coverage.
- `EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests` uses
  PostgreSQL Testcontainers for provider behavior, concurrent appends,
  per-tenant offsets, and worker lease fencing.

SQLite is useful for local integration tests, but it cannot prove PostgreSQL
transaction isolation or multi-instance behavior.

`EventLoomSqliteTestHost` (in `EventLoom.Testing`) wraps that SQLite setup: a
kept-open in-memory connection, a fully wired EventLoom container, schema
creation, and deterministic time (`ManualTimeProvider`) and event identifiers
(`SequentialEventIdGenerator`).

```csharp
await using var host = await EventLoomSqliteTestHost.CreateAsync(options =>
    options.ConfigureEventLoom = builder => builder
        .AddEvent<OrderPlaced>()
        .AddAggregate<Order, Guid>(aggregate => aggregate
            .ConstructWith(id => new Order(id))
            .UseStream("order", id => id.ToString("D"))));

await host.RunScopedAsync(async (services, cancellationToken) =>
{
    var repository = services.GetRequiredService<AggregateRepository<Order, Guid>>();
    var order = new Order(Guid.NewGuid());
    order.Place("coffee", 2);
    await repository.SaveAsync(order);
});
```

This host does not include managed PostgreSQL fixtures: PostgreSQL
integration tests exist specifically to prove distributed behavior that a
single in-process container cannot represent, so they use Testcontainers
directly instead of a shared managed host. See below.

## Test a PostgreSQL application path

Use Testcontainers for every PostgreSQL integration test that claims
distributed correctness:

```csharp
await using var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
await container.StartAsync();
```

Create each simulated application instance with an independent
`EventStoreDbContext`. Test competing stream appends, first-write races,
tenant-isolated reads, idempotent append retries, and checkpoint advancement.

The current repository needs `TESTCONTAINERS_RYUK_DISABLED=true` on Docker
Desktop setups that block Testcontainers' Ryuk resource reaper. Use that
workaround only when Docker lifecycle cleanup is otherwise guaranteed.

## What to assert

| Behavior | Assert |
| --- | --- |
| Aggregate command | Event type, payload, state, and pending event count. |
| Replay | Same state after loading persisted history. |
| Append | All events appear once with consecutive stream versions. |
| Concurrency | One command succeeds; the other is a visible conflict or reevaluated retry. |
| Tenant access | A tenant never reads another tenant's stream or offsets. |
| Idempotency | The same append ID returns the original envelopes. |
| Projection | Checkpoint advances only after a handler succeeds; EF read-model effects are atomic with it. |
| Outbox | Event and message commit together; retries reuse the stable message ID and persist attempt history. |

Snapshot replay and projection storage are covered by SQLite integration tests.
The hosted projection worker has restart, retry, pause, and inline-transaction
coverage; PostgreSQL integration tests cover stale lease fencing. Outbox
delivery is covered with file-backed SQLite persistence, an idempotent retrying
publisher, and PostgreSQL stale-lease fencing.
