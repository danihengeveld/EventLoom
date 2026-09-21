---
title: Configuration reference
description: Complete reference for EventLoom composition methods, options, defaults, and validation rules.
---

This page is a lookup reference for the public configuration surface. For
worked examples, use the [configuration guide](/guides/configure-ef-core), the
[projections guide](/guides/projections), or the
[outbox guide](/guides/outbox).

## Start from one of these configurations

Use these as minimal, complete composition baselines. Add aggregate,
projection, snapshot, and outbox registrations after choosing the provider.

### PostgreSQL service

```csharp
builder.Services.AddEventLoom(eventLoom => eventLoom
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .UseSingleTenancy()
    .AddEvent<OrderPlaced>()
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D"))));
```

### Multi-tenant PostgreSQL service

```csharp
builder.Services.AddEventLoom(eventLoom => eventLoom
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .UseMultiTenancy<AuthenticatedTenantAccessor>()
    .AddEvent<OrderPlaced>());
```

### Local or single-process SQLite application

```csharp
builder.Services.AddEventLoom(eventLoom => eventLoom
    .UseSqlite("Data Source=eventloom.db")
    .UseSingleTenancy()
    .AddEvent<OrderPlaced>());
```

Choose one provider per application. Use PostgreSQL when separate processes
can write, project, or publish against the same event store; SQLite is limited
to a controlled single process.

## Service composition

| Method | Purpose |
| --- | --- |
| `services.AddEventLoom()` | Starts fluent registration and returns an `EventLoomBuilder`. |
| `services.AddEventLoom(configure)` | Runs configuration and validates it before returning the service collection. |
| `AddEvent<TEvent>()` | Registers one concrete persisted event. |
| `AddEventsFromAssembly(assembly)` / `AddEventsFromAssemblyContaining<T>()` | Registers concrete event types from an assembly. |
| `AddUpcaster(upcaster)` | Registers one deterministic event-payload upcaster. |
| `ConfigureEventSerialization(configure)` | Configures JSON serialization for persisted events. |
| `UseSingleTenancy(tenantId)` | Uses one stable tenant; defaults to `default`. |
| `UseMultiTenancy<TAccessor>()` | Enables multi-tenancy and registers a scoped `ITenantAccessor`. |
| `UseMultiTenancy()` | Enables multi-tenancy when the application registers `ITenantAccessor` itself. |
| `ConfigureEventStore(configure)` | Configures table prefix, schema, and tenancy options. |
| `ConfigureWorkers(configure)` | Configures asynchronous projection workers. |
| `ConfigureSnapshotRetention(policy)` | Sets the default snapshot retention policy. |
| `ConfigureProjectionModel(configure)` | Adds EF Core mappings for transactional projection read models. |
| `AddProjection(name, configure, version)` | Registers one named projection with one or more handlers. |
| `AddOutboxPublisher<TPublisher>(configure)` | Enables transactional outbox messages and registers the publisher worker. |
| `UseTimeProvider(provider)` | Replaces the system clock; useful for deterministic tests. |

`AddProjection(name, configure, version)` is the preferred projection API. Its
registration builder makes the durable projection name and version explicit and
chooses `Asynchronous`, `Transactional`, or `Inline` per handler.

## Provider selection

Select exactly one storage provider.

| Method | Effect |
| --- | --- |
| `UsePostgreSql(connectionString[, configure])` | Configures PostgreSQL, enables schemas, and installs the PostgreSQL retry policy. |
| `UsePostgreSql(connectionFactory[, configure])` | Uses a scoped `DbConnection`; required when sharing an application transaction. |
| `UseSqlite(connectionString[, configure])` | Configures SQLite, initializes its bundled native dependency, and disables schemas. |
| `UseSqlite(connectionFactory[, configure])` | Uses a scoped SQLite `DbConnection`; required when sharing an application transaction. |

The optional provider callback configures the normal EF Core provider options.
Provider selection owns `UseSchema`: PostgreSQL enables it and SQLite disables
it. Do not override that setting in `ConfigureEventStore`.

## `EventStoreOptions`

| Property | Default | Rules and guidance |
| --- | --- | --- |
| `Schema` | `eventloom` | PostgreSQL schema name. Change only through a data migration after data exists. |
| `TablePrefix` | `eventloom_` | Prefix for every EventLoom table. Keep stable after storage is created. |

Tenancy and schema support are configured through `UseSingleTenancy`,
`UseMultiTenancy`, and the selected provider rather than by mutating storage
options.

## `EventSerializationOptions`

| Property | Default | Rules and guidance |
| --- | --- | --- |
| `Converters` | Empty | Applied in registration order. Keep each converter compatible with every persisted payload it handles. |
| `PropertyNamingPolicy` | `CamelCase` | Treat as persisted payload compatibility. |
| `PropertyNameCaseInsensitive` | `false` | Strict matching exposes payload drift. |
| `NumberHandling` | `Strict` | Do not loosen without a deliberate stored-data compatibility decision. |
| `ReferenceHandler` | `null` | Events should normally be acyclic; reference metadata changes the payload contract. |

EventLoom validates registered events during configuration. Treat every
serializer option and converter as part of the persisted-data contract.

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

Declare an immutable snapshot DTO as `IAggregateSnapshot<TAggregate>`, then
call `UseSnapshots<TSnapshot>(...)` in the aggregate registration. The
aggregate must supply private `CreateSnapshot(): TSnapshot` and
`RestoreSnapshot(TSnapshot)` methods. The typed builder accepts:

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
