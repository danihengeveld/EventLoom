---
title: Configure the event store
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

builder.Services.AddEventLoom(eventLoom => eventLoom
    .RegisterEvent<OrderPlaced>()
    .AddAggregateRepository<Order, Guid>(
        id => new Order(id),
        "order",
        id => id.ToString("D"))
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("EventStore")!));
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

builder.Services.AddEventLoom(eventLoom => eventLoom
    .RegisterEvent<OrderPlaced>()
    .UseSqlite("Data Source=eventloom.db"));
```

`UseSqlite` disables schemas and initializes the SQLite provider. It does not
support distributed workers; use PostgreSQL for multi-instance correctness.

## Storage names and tenancy

Configure these before selecting the provider:

```csharp
builder.Services.AddEventLoom(eventLoom => eventLoom
    .ConfigureTenancy(TenancyMode.Required)
    .ConfigureEventStore(options =>
    {
        options.Schema = "events";
        options.TablePrefix = "app_";
    })
    .RegisterEvent<OrderPlaced>()
    .UsePostgreSql(connectionString));
```

| `EventStoreOptions` property | Default | Notes |
| --- | --- | --- |
| `TenancyMode` | `Disabled` | Set to `Required` with a scoped `ITenantAccessor`. |
| `Schema` | `eventloom` | PostgreSQL only. |
| `TablePrefix` | `eventloom_` | Applied to every EventLoom table. |
| `UseSchema` | `false` | Set automatically by the provider extension. |

Call `ConfigureEventStore` before `UsePostgreSql` or `UseSqlite`. The selected
provider owns `UseSchema`: PostgreSQL enables it, while SQLite disables it.

## Worker and retry settings

Configure worker primitives when an application starts workers that use
`WorkerLeaseStore`:

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

EventLoom validates these values during registration. Registering an
asynchronous projection adds its checkpointed hosted worker automatically.
PostgreSQL safely supports multiple worker instances; SQLite is limited to one
controlled application instance. EventLoom does not provide an outbox publisher
yet.

## Schema creation and migrations

The current pre-release includes the EF Core model but does not ship
EventLoom-owned migrations. The sample uses `EnsureCreatedAsync` for a local
empty database:

```csharp
await using var scope = app.Services.CreateAsyncScope();
var storeContext = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
await storeContext.Database.EnsureCreatedAsync();
```

Do not call `EnsureCreatedAsync` against a database managed by migrations. For
production, create and review migrations in the host application against the
dedicated `EventStoreDbContext`, then apply them through your deployment
process. `EventStoreSchema.MigrateAsync` is available for the eventual
migration-based path but has no EventLoom migrations to apply in the current
release.

`EventStoreSchema.GetTableNames(options)` returns the table names EventLoom
manages for diagnostics and health checks.
