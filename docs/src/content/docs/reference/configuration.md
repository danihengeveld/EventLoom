---
title: Configuration reference
description: Reference for EventLoom hosting, storage, tenancy, worker, and repository configuration.
---

## Composition methods

| Method | Purpose |
| --- | --- |
| `AddEventLoom()` | Starts fluent EventLoom service registration. Exactly one provider must be selected. |
| `AddEvent<TEvent>()` | Registers one persisted event type explicitly. |
| `AddEventsFromAssemblyContaining<T>()` | Opt-in registration of every concrete event in an assembly. |
| `AddJsonSerializerContext(context)` | Adds source-generated `System.Text.Json` metadata. |
| `ConfigureEventSerialization(configure)` | Configures resolver composition, reflection fallback, JSON naming, converters, number handling, and reference handling. |
| `AddUpcaster(upcaster)` | Adds one deterministic historical-payload transformation. |
| `UseSingleTenancy(tenantId)` | Uses one stable internal tenant; this is the default. |
| `UseMultiTenancy<TAccessor>()` | Enables scoped multi-tenancy and registers its accessor. |
| `ConfigureEventStore(configure)` | Sets schema and table-prefix options. |
| `ConfigureWorkers(configure)` | Sets projection worker lease and retry values. |
| `ConfigureSnapshotRetention(policy)` | Retains the requested number of recent snapshots per aggregate stream. |
| `ConfigureProjectionModel(configure)` | Maps EF read-model entities used by transactional projections. |
| `AddProjection(name, configure, version)` | Registers named asynchronous, transactional, or inline handlers that share one durable identity. |
| `AddProjection<THandler, TEvent>(name, version)` | Registers an asynchronous at-least-once handler and its worker. |
| `AddEfProjection<THandler, TEvent>(name, version)` | Registers a handler whose read-model update and checkpoint are atomic. |
| `AddInlineProjection<THandler, TEvent>(name, version)` | Registers a handler inside the event append transaction. |
| `AddOutboxPublisher<TPublisher>(configure)` | Enables transactional outbox writes and starts the publisher worker; optionally configures its instance identity, polling, batch size, lease, retry, and successful-delivery retention (the default deletes immediately). |
| `UseTimeProvider(provider)` | Replaces the system clock for deterministic behavior. |
| `UsePostgreSql(connectionString)` | Selects PostgreSQL, schemas, and retry policy. |
| `UseSqlite(connectionString)` | Selects SQLite and disables schemas. |

## `EventStoreOptions`

## `EventSerializationOptions`

| Property | Default | Guidance |
| --- | --- | --- |
| `Contexts` | Empty | Add source-generated contexts in deterministic order; all contexts are composed. |
| `TypeInfoResolver` | `null` | Adds one resolver after registered contexts. |
| `ReflectionFallback` | `true` | Set `false` for trimming/AOT deployments and register metadata for every event. |
| `PropertyNamingPolicy` | `CamelCase` | Keep stable for persisted payload compatibility. |
| `PropertyNameCaseInsensitive` | `false` | Keep strict to detect payload contract drift. |
| `NumberHandling` | `Strict` | Avoid permissive named floating-point literals or quoted numbers unless required. |
| `ReferenceHandler` | `null` | Events should normally be acyclic; opt into reference metadata only for an intentional contract. |

EventLoom validates every registered event during `AddEventLoom(...)`. If strict
source generation is enabled without metadata, startup fails with the stable
event name, version, CLR type, and remediation guidance.

| Property | Default | Valid use |
| --- | --- | --- |
| `TenancyMode` | `SingleTenant` | Use `MultiTenant` with a scoped `ITenantAccessor`. |
| `SingleTenantId` | `default` | Stable persisted tenant identity for a single-tenant application. |
| `Schema` | `eventloom` | PostgreSQL schema name. |
| `TablePrefix` | `eventloom_` | Prefix for all EventLoom tables. |
| `UseSchema` | `false` | Provider-managed; do not override after provider selection. |

## `OutboxOptions`

| Property | Default | Valid use |
| --- | --- | --- |
| `SuccessfulDeliveryRetention` | `TimeSpan.Zero` | Keep successful messages and attempts for a non-negative duration before bounded cleanup. |

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
eventLoom.AddAggregate<Order, OrderId>(aggregate => aggregate
    .ConstructWith(id => new Order(id))
    .UseStream("order", id => id.Value.ToString("N")));
```

The configured repository uses EventLoom's internal tenant in single-tenant
mode. In multi-tenant mode, `LoadAsync(id)` and `SaveAsync(aggregate)` require
the configured scoped accessor. Explicit tenant overloads remain available for
background and administrative operations.

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
