---
title: Configuration reference
description: Complete reference for EventLoom composition methods, options, defaults, and validation rules.
---

This page is a lookup reference for the public configuration surface. For
worked examples, use [Configure the EF Core store](/guides/configure-ef-core),
[Use MongoDB](/guides/use-mongodb), [Build projections](/guides/projections),
or [Publish integration messages](/guides/outbox).

## Start from one of these configurations

### PostgreSQL service

```csharp
builder.Services.AddEventLoom(eventLoom => eventLoom
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .UseSingleTenancy()
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D"))));
```

### MongoDB service

```csharp
builder.Services.AddEventLoom(eventLoom => eventLoom
    .UseMongoDb(
        builder.Configuration.GetConnectionString("EventStore")!,
        databaseName: "eventloom")
    .UseSingleTenancy()
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D"))));
```

### Local or single-process SQLite application

```csharp
builder.Services.AddEventLoom(eventLoom => eventLoom
    .UseSqlite("Data Source=eventloom.db")
    .UseSingleTenancy()
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D"))));
```

Choose one provider per application.

## Service composition

| Method | Purpose |
| --- | --- |
| `services.AddEventLoom()` | Starts fluent registration and returns an `EventLoomBuilder`. |
| `services.AddEventLoom(configure)` | Runs configuration and validates it before returning the service collection. |
| `AddAggregate<TAggregate, TId>(configure)` | Registers a repository, stream mapping, factory, and all events owned by the aggregate. |
| `AddAggregateEvents<TAggregate>()` | Registers events owned by an aggregate without registering a repository. |
| `AddUpcaster(upcaster)` | Registers one deterministic event-payload upcaster. |
| `ConfigureEventSerialization(configure)` | Configures JSON serialization for persisted events. |
| `UseStorage(capabilities, register)` | Extension point for custom storage providers. Applications normally use a provider package instead. |
| `UseSingleTenancy(tenantId)` | Uses one stable tenant; defaults to `default`. |
| `UseMultiTenancy<TAccessor>()` | Enables multi-tenancy and registers a scoped `ITenantAccessor`. |
| `UseMultiTenancy()` | Enables multi-tenancy when the application registers `ITenantAccessor` itself. |
| `ConfigureWorkers(configure)` | Configures asynchronous projection workers. |
| `ConfigureSnapshotRetention(policy)` | Sets the default snapshot retention policy. |
| `AddProjection(name, configure, version)` | Registers one named projection with one or more handlers. |
| `AddOutboxPublisher<TPublisher>(configure)` | Enables transactional outbox messages and registers the publisher worker. |
| `UseTimeProvider(provider)` | Replaces the system clock; useful for deterministic tests. |

`AddProjection(name, configure, version)` is the preferred projection API. Its
registration builder makes the durable projection name and version explicit and
chooses `Asynchronous`, provider-specific `Transactional`, or `Inline` per
handler.

## Provider selection

Select exactly one storage provider.

| Method | Effect |
| --- | --- |
| `UsePostgreSql(connectionString[, configure])` | Configures PostgreSQL, enables schemas, and installs the PostgreSQL retry policy. |
| `UsePostgreSql(connectionFactory[, configure])` | Uses a scoped `DbConnection`; required when sharing a relational unit of work. |
| `UseSqlite(connectionString[, configure])` | Configures SQLite, initializes its bundled native dependency, and disables schemas. |
| `UseSqlite(connectionFactory[, configure])` | Uses a scoped SQLite `DbConnection`; required when sharing a relational unit of work. |
| `UseMongoDb(connectionString, databaseName[, configure])` | Configures MongoDB storage for one database and installs the MongoDB retry policy. |
| `UseMongoDb(clientFactory, databaseName[, configure])` | Uses an application-supplied `IMongoClient`. |

Provider packages also add provider-specific APIs:

- EF Core: `ConfigureEntityFramework(...)`, `ConfigureProjectionModel(...)`,
  `IEfProjectionHandler<TEvent>`, `unitOfWork.EnlistAsync(...)`, and
  `unitOfWork.DbTransaction`
- MongoDB: `IMongoProjectionHandler<TEvent>`, `MongoSessionAccessor`, and
  `unitOfWork.Session`

## `EntityFrameworkStorageOptions`

Use `ConfigureEntityFramework(...)` from `EventLoom.EntityFrameworkCore`.

| Property | Default | Rules and guidance |
| --- | --- | --- |
| `Schema` | `eventloom` | PostgreSQL schema name. Keep stable after data exists. Ignored by SQLite. |
| `TablePrefix` | `eventloom_` | Prefix for every EventLoom table. Keep stable after storage is created. |

Schema support is provider-owned: PostgreSQL enables it, SQLite disables it.

## `MongoDbStorageOptions`

Pass these options to `UseMongoDb(..., configure)`.

| Property | Default | Rules and guidance |
| --- | --- | --- |
| `CollectionPrefix` | `eventloom_` | Prefix for every EventLoom collection. Keep stable after data exists. |
| `TransactionTimeout` | 30 seconds | Must be positive. Sets the maximum MongoDB transaction commit time EventLoom requests. |
| `RegisterStandardGuidSerializer` | `true` | Best-effort process-wide registration of `GuidSerializer(GuidRepresentation.Standard)` so application documents can hold `Guid` values. Set `false` when the application registers its own `Guid` serializer. |

## `EventSerializationOptions`

| Property | Default | Rules and guidance |
| --- | --- | --- |
| `Converters` | Empty | Applied in registration order. Keep each converter compatible with every persisted payload it handles. |
| `PropertyNamingPolicy` | `CamelCase` | Treat as persisted payload compatibility. |
| `PropertyNameCaseInsensitive` | `false` | Strict matching exposes payload drift. |
| `NumberHandling` | `Strict` | Do not loosen without a deliberate stored-data compatibility decision. |
| `ReferenceHandler` | `null` | Events should normally be acyclic; reference metadata changes the payload contract. |

## `EventStoreWorkerOptions`

`ConfigureWorkers` applies only to asynchronous projection workers.

| Property | Default | Validation |
| --- | --- | --- |
| `InstanceId` | Generated process identity | Nonempty and unique for each active process. |
| `PollInterval` | 1 second | Positive. |
| `BatchSize` | 100 | 1 through 10,000. |
| `LeaseDuration` | 30 seconds | Positive and longer than `LeaseRenewalInterval`. |
| `LeaseRenewalInterval` | 10 seconds | Positive and shorter than `LeaseDuration`. |
| `MaxRetryAttempts` | 5 | 0 through 100. |

## `OutboxOptions`

Pass these options to `AddOutboxPublisher`. They do not inherit
`ConfigureWorkers` settings.

| Property | Default | Validation |
| --- | --- | --- |
| `InstanceId` | Generated process identity | Nonempty and unique for each active process. |
| `PollInterval` | 1 second | Positive. |
| `BatchSize` | 100 | 1 through 10,000. |
| `LeaseDuration` | 30 seconds | Positive and longer than `LeaseRenewalInterval`. |
| `LeaseRenewalInterval` | 10 seconds | Positive and shorter than `LeaseDuration`. |
| `MaxRetryAttempts` | 5 | 0 through 100. |
| `SuccessfulDeliveryRetention` | `TimeSpan.Zero` | Non-negative. Zero deletes a successfully delivered message and its attempts immediately. |

## Snapshot configuration

Declare an immutable snapshot DTO as
`IAggregateSnapshot<TSnapshot, TAggregate>`, implement
`ISnapshotable<TSnapshot>` on the aggregate, then call
`UseSnapshots<TSnapshot>(...)` or `UseSnapshots<TSnapshot>()` in the aggregate
registration. The typed builder accepts:

| Method | Effect |
| --- | --- |
| `Every(interval)` | Captures every positive number of events. |
| `UsePolicy(policy)` | Uses custom snapshot cadence. |
| `KeepLatest(count)` | Retains a positive number of recent snapshots. |
| `UseRetention(policy)` | Uses custom retention. |
| `UseUpcasters(upcasters)` | Registers deterministic migrations for earlier snapshot schemas. |
| `UseInvalidator(invalidator)` | Removes an unusable snapshot after EventLoom safely falls back to full replay. |

The default capture cadence is every 100 events. The default retention policy
keeps the latest snapshot for each tenant and aggregate stream.

## Store bootstrap and validation

Resolve `IStorageSchema` when you need provider-neutral bootstrap or validation:

| Method | Effect |
| --- | --- |
| `EnsureCreatedAsync()` | Creates the provider's storage objects when they do not already exist. Never migrates existing data. |
| `ValidateAsync()` | Returns `CanConnect`, `MissingCount`, `IncompatibleCount`, and `IsCompatible`. |

Use `InitializeEventLoomDevelopmentDatabaseAsync()` only for development
bootstrap in ASP.NET Core applications.
