---
slug: v0.1/guides/configure-ef-core
title: Configure event storage
description: Register EventLoom with PostgreSQL or SQLite and customize event-store storage.
---

Use the hosting composition API for normal applications. It registers the
event registry, serializer, UUIDv7 generator, time provider, dedicated
`EventStoreDbContext`, `EventStore`, worker-lease store, and configured
aggregate repositories.

## PostgreSQL

PostgreSQL is required for multiple application instances and distributed
workers:

```csharp
using EventLoom;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;

builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddEvent<OrderPlaced>()
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
```

`UsePostgreSql` enables the default `eventloom` schema and adds the bounded
PostgreSQL retry policy. It retries only transient connection failures,
deadlocks, and serialization failures. Unique-constraint and logical
expected-version conflicts remain visible to the caller.

Pass the provider's options callback when required:

```csharp
eventLoom.UsePostgreSql(connectionString, npgsql =>
    npgsql.CommandTimeout(30));
```

## SQLite

SQLite is for local development, tests, embedded applications, and controlled
single-node deployments:

```csharp
using EventLoom.EntityFrameworkCore.Sqlite;

builder.Services
    .AddEventLoom()
    .UseSqlite("Data Source=eventloom.db")
    .AddEvent<OrderPlaced>();
```

`UseSqlite` disables schemas and initializes the SQLite provider from its
bundled native dependency, so applications do not need a system SQLite library.
It does not support distributed workers; use PostgreSQL for multi-instance
correctness.

## Storage names and tenancy

Configure these before selecting the provider:

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(connectionString)
    .UseMultiTenancy<AuthenticatedTenantAccessor>()
    .ConfigureEventStore(options =>
    {
        options.Schema = "events";
        options.TablePrefix = "app_";
    })
    .AddEvent<OrderPlaced>();
```

| `EventStoreOptions` property | Default | Notes |
| --- | --- | --- |
| `Schema` | `eventloom` | PostgreSQL only. |
| `TablePrefix` | `eventloom_` | Applied to every EventLoom table. |

Configure tenancy with `UseSingleTenancy`, `UseMultiTenancy<TAccessor>`, or
`UseMultiTenancy()` when the application registers `ITenantAccessor` itself.
The selected provider owns schema support: PostgreSQL enables it, while SQLite
disables it.

## Worker and retry settings

Both the projection worker and the outbox worker use fenced, tenant-scoped
leases, but each has its own dedicated options so their polling, batching,
lease, and retry behavior can be tuned independently.

Configure the projection worker with `ConfigureWorkers`:

```csharp
eventLoom.ConfigureWorkers(options =>
{
    options.InstanceId = Environment.MachineName;
    options.PollInterval = TimeSpan.FromSeconds(1);
    options.BatchSize = 100;
    options.LeaseDuration = TimeSpan.FromSeconds(30);
    options.LeaseRenewalInterval = TimeSpan.FromSeconds(10);
    options.MaxRetryAttempts = 5;
});
```

Configure the outbox worker through `AddOutboxPublisher`'s optional callback
instead; it groups the same worker controls together with successful-delivery
retention. See [Outbox and application integration](./outbox/) to register a
publisher.

EventLoom validates these values during registration. Registering an
asynchronous projection adds the projection worker automatically; registering
an outbox publisher adds the outbox worker automatically. PostgreSQL safely
supports multiple worker instances; SQLite is limited to one controlled
application instance.

## OpenTelemetry

`EventLoom.Hosting` provides optional convenience registration for the
dependency-free EventLoom activity source and meter. See
[Observability](./observability/) for tracing, metrics, and data-safety
guidance.

## Schema creation and migrations

The current pre-release includes the EF Core model but does not ship static
provider migrations because schema and table names are configurable. The
sample explicitly initializes a local empty database:

```csharp
if (app.Environment.IsDevelopment())
{
    await app.InitializeEventLoomDevelopmentDatabaseAsync();
}
```

The helper refuses to run outside Development and does not migrate an existing
schema. For production, create and review migrations in the host application
against the dedicated `EventStoreDbContext`, then apply them through your
deployment process. `EventStoreSchema.MigrateAsync` applies those host-owned
migrations when an application-controlled migration step is appropriate.

`EventStoreSchema.GetTableNames(options)` returns the table names EventLoom
manages for diagnostics and health checks.
