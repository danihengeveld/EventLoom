---
title: Guarantees and operational APIs
description: Authoritative delivery guarantees, storage-provider rules, and administrative API boundaries.
---

## Guarantees at a glance

| Area | Guarantee | Application responsibility |
| --- | --- | --- |
| Append | A batch of events commits atomically with stream versions and tenant offsets. | Supply the correct expected version and preserve a command's append ID when retrying an ambiguous outcome. |
| Aggregate repository | A successful non-idempotent save clears pending events. | Keep aggregate state changes inside `Apply` methods. |
| Append replay | Reusing the same `AppendId` in one tenant returns the original append result. | Keep append IDs unique per tenant and command. |
| Tenant order | Events have consecutive, committed `TenantOffset` values per tenant. | Scope checkpoints and background reads to one tenant. |
| Transactional projection | Handler changes and checkpoint commit atomically within the provider transaction. | Use the provider-supplied transaction object (`EventStoreDbContext` or `MongoProjectionTransaction`) correctly. |
| Asynchronous projection | At-least-once delivery. | Make every handler idempotent. |
| Outbox publication | Event and message commit together; external publication is at least once. | Deduplicate at the destination with `OutboxMessage.MessageId`. |
| Worker leases | Fencing tokens increase monotonically across acquire, renew, and release. | Treat a lost lease as a failed ownership claim and reacquire work explicitly. |
| Snapshot | Snapshot failures never modify committed event history; unusable snapshots fall back to replay. | Keep aggregate snapshot methods and upcasters deterministic. |

## Provider-neutral storage contract

Every storage provider EventLoom supports must:

- make each append atomic;
- enforce optimistic concurrency per stream;
- assign gapless per-tenant offsets that become visible in commit order;
- scope every operation to a tenant;
- fence projection and outbox progress with monotonically increasing lease
  tokens;
- expose schema bootstrap and compatibility checks through `IStorageSchema`.

The shared conformance suite in `tests/EventLoom.Storage.Conformance` verifies
these rules against every built-in provider.

## Ordering and identity

- `StreamVersion` orders events inside one aggregate stream.
- `TenantOffset` orders committed events inside one tenant and is the position
  for projections and tenant-log readers.
- An event ID is UUIDv7. It is a stable event identity, but not the ordering
  mechanism.
- `AppendId` is a caller-owned idempotency key scoped to a tenant. Reusing the
  same append ID on a different stream in that tenant replays the original
  append result.
- A projection identity is its `ProjectionKey` (`Name` and positive `Version`).
  Increment the version for a new read-model contract.

## Provider-specific guarantees

### PostgreSQL and SQLite

- Both relational providers use the EF Core implementation.
- Transactional projections implement `IEfProjectionHandler<TEvent>` and receive
  `EventStoreDbContext`.
- Shared units of work can enlist an application `DbContext` through
  `unitOfWork.EnlistAsync(...)` when both contexts share the same scoped
  `DbConnection`.
- SQLite is explicitly single-node only.

### MongoDB

- The provider uses `MongoDB.Driver` directly, not EF Core.
- Transactional projections implement `IMongoProjectionHandler<TEvent>` and
  receive `MongoProjectionTransaction` with `.Session` and `.Database`.
- Inline projections can access the append session through
  `MongoSessionAccessor`.
- Shared units of work expose the provider session through `unitOfWork.Session`.
- A standalone `mongod` is incompatible because EventLoom requires
  multi-document transactions.

## Schema and health data

`IStorageSchema.ValidateAsync()` returns:

- `CanConnect`
- `MissingCount`
- `IncompatibleCount`
- `IsCompatible`

EventLoom health checks surface incompatible storage through the data keys:

- `missing_object_count`
- `incompatible_object_count`

## Projection administration

`ProjectionAdministration` always requires an explicit tenant and
`ProjectionKey`.

| Method | Effect | Use it when |
| --- | --- | --- |
| `GetCheckpointAsync` | Reads the checkpoint and status. | Diagnosing lag or confirming recovery. |
| `ReadFailuresAsync` | Lists persisted failures; resolved failures are excluded by default. | Investigating a paused projection. |
| `ResumeAsync` | Makes a paused projection eligible to retry. | The handler, dependency, or data issue is fixed. |
| `SkipAsync` | Marks one failed event resolved and advances past it. | An authorized operator accepts a permanent read-model gap. |
| `ReplayAsync` | Resets the checkpoint to offset zero. | Rebuilding a projection version's read model. |

`ReplayAsync` never clears a read model. For production rebuilds, deploy a new
projection version that writes to a new or shadow table or collection, let it
catch up, validate it, then switch readers.

## Outbox administration

`OutboxAdministration.GetAsync(tenantId, messageId)` reads one retained
message. `ReadAttemptsAsync(tenantId, messageId)` reads its delivery history.
Successful messages and attempts are deleted immediately by default; configure a
positive `SuccessfulDeliveryRetention` to make them inspectable. Pending and
failed messages are never automatically removed.

The durable outbox worker lease name is `outbox:publisher`.

## ASP.NET Core diagnostics boundary

`MapEventLoomAdminDiagnostics(...)` intentionally exposes only aggregate schema,
projection, and outbox summaries. It does not expose tenant IDs, event IDs,
payloads, metadata, or repair actions. Keep tenant-scoped inspection and repair
APIs behind your own authenticated and audited application boundary.
