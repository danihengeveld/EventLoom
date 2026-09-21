# EventLoom.Testing

`EventLoom.Testing` provides supported test-authoring helpers for EventLoom
applications:

- **`AggregateScenario<TAggregate, TId>`** — a Given/When/Then API for testing
  aggregate command handling without a database: replay prior history, invoke
  one command, and assert the raised events, resulting state, or a thrown
  exception.
- **`EventLoomSqliteTestHost`** — a managed, fully wired EventLoom
  dependency-injection container backed by a kept-open in-memory SQLite
  connection, with schema initialization and deterministic time (
  `ManualTimeProvider`) and event identifiers (`SequentialEventIdGenerator`)
  for integration tests that exercise real persistence.

## Aggregate scenarios

```csharp
using EventLoom.Testing;

var scenario = AggregateScenario.For<Order, Guid>(id => new Order(id))
    .Given(orderId, new OrderPlaced("coffee", 2))
    .When(order => order.Cancel("out of stock"));

await Assert.That(scenario.RaisedEvents.Single()).IsTypeOf<OrderCancelled>();
await Assert.That(scenario.Aggregate.Status).IsEqualTo("cancelled");
```

`Given` replays history without adding it to the aggregate's pending events.
`When` invokes one command and captures any thrown exception for
`ThenThrows<TException>()` instead of letting it propagate. `ThenEvents` and
`Then` assert against the raised events and the aggregate's public state.

## SQLite test host

```csharp
using EventLoom.Testing;

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

host.Clock.Advance(TimeSpan.FromMinutes(5));
```

The host keeps one SQLite connection open for its lifetime, because an
in-memory SQLite database is destroyed once its last connection closes, and
creates the event-store schema during `CreateAsync`. Disposing the host
disposes its dependency-injection container and closes the connection.
`ConfigureEventLoom` registers events, aggregates, and projections the same
way production composition does; the host applies `UseSqlite`, tenancy, and
the deterministic time provider itself.

## Boundary: no managed PostgreSQL test host

This package intentionally does not include a managed PostgreSQL test host.
PostgreSQL integration tests need Testcontainers-managed server lifecycle and
exist to prove distributed behavior (concurrent appends across simulated
application instances, worker lease fencing under contention) that a shared
in-process container cannot represent. Use
[Testcontainers.PostgreSql](https://dotnet.testcontainers.org/) directly, as
`EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests` does, for that
coverage.

See the [testing guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/concepts/testing.md)
for the recommended domain, relational, and PostgreSQL distributed test
boundaries.
