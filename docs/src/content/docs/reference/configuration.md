---
title: Configuration reference
description: Reference for EventLoom hosting, storage, tenancy, worker, and repository configuration.
---

## Composition methods

| Method | Purpose |
| --- | --- |
| `AddEventLoom(configure)` | Starts EventLoom service registration. A provider must be selected inside `configure`. |
| `RegisterEvent<TEvent>()` | Registers one persisted event type explicitly. |
| `RegisterEventsFromAssembly(assembly)` | Opt-in registration of every concrete event in an assembly. |
| `AddJsonSerializerContext(context)` | Adds source-generated `System.Text.Json` metadata. |
| `AddUpcaster(upcaster)` | Adds one deterministic historical-payload transformation. |
| `ConfigureTenancy(mode)` | Chooses disabled or required tenancy enforcement. |
| `ConfigureEventStore(configure)` | Sets schema and table-prefix options. |
| `ConfigureWorkers(configure)` | Sets worker lease and retry values. |
| `ConfigureSnapshotRetention(policy)` | Retains the requested number of recent snapshots per aggregate stream. |
| `ConfigureProjectionModel(configure)` | Maps EF read-model entities used by transactional projections. |
| `AddProjection<THandler, TEvent>(name, version)` | Registers an asynchronous at-least-once handler and its worker. |
| `AddEfProjection<THandler, TEvent>(name, version)` | Registers a handler whose read-model update and checkpoint are atomic. |
| `AddInlineProjection<THandler, TEvent>(name, version)` | Registers a handler inside the event append transaction. |
| `UseTimeProvider(provider)` | Replaces the system clock for deterministic behavior. |
| `UsePostgreSql(connectionString)` | Selects PostgreSQL, schemas, and retry policy. |
| `UseSqlite(connectionString)` | Selects SQLite and disables schemas. |

## `EventStoreOptions`

| Property | Default | Valid use |
| --- | --- | --- |
| `TenancyMode` | `Disabled` | Set `Required` with a scoped `ITenantAccessor`. |
| `Schema` | `eventloom` | PostgreSQL schema name. |
| `TablePrefix` | `eventloom_` | Prefix for all EventLoom tables. |
| `UseSchema` | `false` | Provider-managed; do not override after provider selection. |

## `EventStoreWorkerOptions`

| Property | Default | Constraint |
| --- | --- | --- |
| `InstanceId` | Machine name | Nonempty, unique per active process. |
| `PollInterval` | 1 second | Positive. |
| `BatchSize` | 100 | 1 through 10,000. |
| `LeaseDuration` | 30 seconds | Positive and longer than renewal interval. |
| `LeaseRenewalInterval` | 10 seconds | Positive and shorter than lease duration. |
| `MaxRetryAttempts` | 5 | 0 through 100. |

## Repository registrations

Use the short application path:

```csharp
eventLoom.AddAggregateRepository<Order, OrderId>(
    id => new Order(id),
    aggregateType: "order",
    streamId: id => id.Value.ToString("N"));
```

The configured repository requires a scoped `ITenantAccessor` for
`LoadAsync(id)` and `SaveAsync(aggregate)`. Use the constructor with explicit
persistence delegates and its explicit load/save overloads when a background
or administrative operation must own all identity inputs.

## Defaults worth preserving

- Event names and aggregate type strings are stable persisted contracts.
- Event schema version defaults to `1`.
- Reflection JSON serialization is enabled unless advanced composition
  constructs `EventSerializer` in strict source-generated mode.
- Snapshot-enabled repositories capture every 100 events by default, and the
  store retains the latest snapshot unless `ConfigureSnapshotRetention` changes it.
- Projection names and versions are durable checkpoint identities. Increment a
  version to rebuild against a new or shadow read model.
- PostgreSQL retries only classified transient conditions.
- SQLite is not a distributed-worker provider.
