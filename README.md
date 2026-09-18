# EventLoom

<p align="center">
  <img src="assets/eventloom-icon.svg" width="128" alt="EventLoom" />
</p>

EventLoom is an opinionated event-sourcing library for .NET 10 and EF Core 10.
It favors explicit contracts and operationally safe defaults: immutable,
versioned events; a dedicated event-store context; short aggregate repository
operations; tenant-scoped ordering; snapshots; checkpointed projections; and a
transactional outbox.

PostgreSQL is the distributed production provider. SQLite supports local,
embedded, and controlled single-node applications.

> **Pre-release:** EventLoom packages are not published to NuGet yet. The
> package names and commands below describe the planned public surface.

## Choose a package

Most web applications should reference `EventLoom.AspNetCore` and exactly one
provider package. The provider brings the core and EF Core infrastructure
dependencies with it.

| Package | Install directly? | Use it for |
| --- | --- | --- |
| `EventLoom` | Yes | Domain event contracts, aggregates, identifiers, and serialization. |
| `EventLoom.EntityFrameworkCore.PostgreSql` | Yes | PostgreSQL event storage and distributed worker correctness. |
| `EventLoom.EntityFrameworkCore.Sqlite` | Yes | Local development, tests, embedded apps, and one controlled process. |
| `EventLoom.EntityFrameworkCore` | No, normally transitive | Shared EF Core storage, aggregate repositories, snapshots, projections, and outbox infrastructure. |
| `EventLoom.AspNetCore` | Yes for web apps | Canonical ASP.NET Core composition, workers, health checks, and endpoint helpers. |
| `EventLoom.Hosting` | Usually transitive | Host-neutral composition and worker implementation. |
| `EventLoom.Testing` | Yes, for test projects | Aggregate Given/When/Then scenarios and a managed SQLite test host. |
| `EventLoom.Analyzers` | Recommended | Build-time validation for persisted EventLoom contracts. |

## Quick start

After the first package release, a PostgreSQL application will start with:

```bash
dotnet add package EventLoom.AspNetCore --prerelease
dotnet add package EventLoom.EntityFrameworkCore.PostgreSql --prerelease
dotnet add package EventLoom.Analyzers --prerelease
```

Define immutable events with stable names and versions, then change aggregate
state exclusively by applying them:

```csharp
using EventLoom;

[EventType("counter.incremented", Version = 1)]
public sealed record CounterIncremented(int Amount) : IDomainEvent;

public sealed class Counter(Guid id) : Aggregate<Guid>(id)
{
    public int Value { get; private set; }

    public void Increment(int amount) => Raise(new CounterIncremented(amount));

    private void Apply(CounterIncremented @event) => Value += @event.Amount;
}
```

Register the event, configured repository, and PostgreSQL provider:

```csharp
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;

builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddEvent<CounterIncremented>()
    .AddAggregate<Counter, Guid>(aggregate => aggregate
        .ConstructWith(id => new Counter(id))
        .UseStream("counter", id => id.ToString("D")));
```

For a local-only application, install
`EventLoom.EntityFrameworkCore.Sqlite` instead and call
`UseSqlite("Data Source=eventloom.db")`. Do not use SQLite to validate
multi-instance or distributed-worker behavior.

## Operational model

- **Append order:** `StreamVersion` provides optimistic concurrency within a
  stream; PostgreSQL tenant offsets provide the committed global ordering for
  projections.
- **Tenancy:** single-tenant by default with an internal stable tenant;
  multi-tenancy is an explicit opt-in with `UseMultiTenancy<TAccessor>()`.
- **Projections and outbox:** asynchronous, at-least-once delivery is the
  default. Consumers must be idempotent; EventLoom does not promise general
  exactly-once external delivery.
- **Snapshots:** application-owned, explicit versioned DTOs reduce replay work
  without replacing event history.
- **Telemetry:** EventLoom exposes dependency-free `ActivitySource` and
  `Meter` instrumentation and excludes payloads and identifiers from default
  attributes.

## Documentation and sample

Start with the [documentation site](docs/README.md), especially the
[installation](docs/src/content/docs/getting-started/installation.md),
[first aggregate](docs/src/content/docs/getting-started/first-aggregate.md),
[EF Core configuration](docs/src/content/docs/guides/configure-ef-core.md),
and [production deployment](docs/src/content/docs/guides/production-deployment.md)
guides. Architectural choices are recorded in
[docs/architecture/decisions](docs/architecture/decisions).

The Ordering API sample runs PostgreSQL and the API through Aspire:

```bash
dotnet run --project samples/EventLoom.Ordering.AppHost
```

Aspire prints a dashboard URL where you can inspect resources, logs, traces,
and metrics. The API also exposes its development-only OpenAPI document at
`/openapi/v1.json` and Scalar reference UI at `/scalar/v1`.

## Development

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
pnpm --dir docs build
```
