---
title: Testing strategy
description: Test event-sourced domain behavior separately from relational and distributed storage behavior.
---

## Test domain behavior without a database

Most application tests should create an aggregate, invoke one command, and
assert the resulting pending events and state:

```csharp
var order = new Order(OrderId.New());

order.Place("coffee", 2);

await Assert.That(order.Status).IsEqualTo("placed");
await Assert.That(order.PendingEvents.Single().Event).IsTypeOf<OrderPlaced>();
```

`EventLoom.Testing` includes `EventTestBuilder<TEvent>` for concise event
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
| Position consumer | Checkpoint advances only after a handler succeeds. |

Snapshots, projection runners, and outbox delivery are not available yet, so
test any application-owned implementation independently until their EventLoom
APIs ship.
