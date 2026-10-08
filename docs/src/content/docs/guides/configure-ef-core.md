---
title: Configure the EF Core store
description: Register EventLoom with PostgreSQL or SQLite and customize the shared EF Core provider.
---

Use this guide when you want the **EF Core storage provider**. If you are still
choosing a backend, start with [Storage providers](/concepts/storage-providers).
If you want MongoDB instead, use [Use MongoDB](/guides/use-mongodb).

The EF Core provider is split across packages:

- `EventLoom.Storage` contains the engine your application code uses;
- `EventLoom.EntityFrameworkCore` implements the storage SPI;
- `EventLoom.EntityFrameworkCore.PostgreSql` and
  `EventLoom.EntityFrameworkCore.Sqlite` select the relational database.

## PostgreSQL

PostgreSQL is the EF Core option for multiple application instances and
distributed workers:

```csharp
using EventLoom;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;

builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
```

`UsePostgreSql` enables the default `eventloom` schema and installs the bounded
PostgreSQL retry policy. It retries transient connection failures, deadlocks,
and serialization failures. Expected-version and other logical conflicts remain
visible to the caller.

Pass the provider callback when you need normal Npgsql EF Core options:

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
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
```

`UseSqlite` disables schemas and initializes SQLite from the package's bundled
native dependency. It does not support distributed workers; use PostgreSQL or
MongoDB for multi-instance correctness.

## Configure storage names and tenancy

Relational naming moved to `ConfigureEntityFramework(...)`:

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(connectionString)
    .UseMultiTenancy<AuthenticatedTenantAccessor>()
    .ConfigureEntityFramework(options =>
    {
        options.Schema = "events";
        options.TablePrefix = "app_";
    })
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
```

| `EntityFrameworkStorageOptions` property | Default | Notes |
| --- | --- | --- |
| `Schema` | `eventloom` | PostgreSQL only. Keep stable after data exists. |
| `TablePrefix` | `eventloom_` | Applied to every EventLoom table. Keep stable after storage is created. |

Configure tenancy with `UseSingleTenancy`, `UseMultiTenancy<TAccessor>`, or
`UseMultiTenancy()` when the application registers `ITenantAccessor` itself.
The selected provider owns schema support: PostgreSQL enables it, while SQLite
disables it.

## Register transactional read models

EF transactional projections map their read models into the dedicated
`EventStoreDbContext` and implement `IEfProjectionHandler<TEvent>`:

```csharp
using EventLoom.EntityFrameworkCore;

builder.Services
    .AddEventLoom()
    .UsePostgreSql(connectionString)
    .ConfigureProjectionModel(modelBuilder =>
        modelBuilder.Entity<OrderSummary>(entity =>
        {
            entity.ToTable("order_summaries");
            entity.HasKey(value => new { value.TenantId, value.OrderId });
        }))
    .AddProjection("orders.summary", projection => projection
        .Transactional<OrderSummaryProjection, OrderPlaced>());
```

Keep EF-specific projection registration in files that import
`EventLoom.EntityFrameworkCore`. If the same file also imports
`EventLoom.MongoDb`, the two provider-specific `Transactional<,>()` extensions
become ambiguous.

## Worker and retry settings

Both the projection worker and the outbox worker use fenced, tenant-scoped
leases, but each has its own options so polling, batching, lease, and retry
behavior can be tuned independently.

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

Configure the outbox worker through `AddOutboxPublisher(...)` instead; see
[Publish integration messages](./outbox/).

## Schema creation and validation

For a new local database, explicitly bootstrap the store in Development:

```csharp
if (app.Environment.IsDevelopment())
{
    await app.InitializeEventLoomDevelopmentDatabaseAsync();
}
```

For validation or deployment tooling, resolve `IStorageSchema` and use the
provider-neutral API:

```csharp
await using var scope = app.Services.CreateAsyncScope();
var schema = scope.ServiceProvider.GetRequiredService<IStorageSchema>();

await schema.EnsureCreatedAsync();
var validation = await schema.ValidateAsync();
```

`ValidateAsync()` returns `CanConnect`, `MissingCount`, `IncompatibleCount`, and
`IsCompatible`. For production PostgreSQL or SQLite deployments, keep using
reviewed, host-owned EF Core migrations for schema changes.
