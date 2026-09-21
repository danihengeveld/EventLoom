---
slug: v0.1/reference/guarantees
title: Guarantees and operational APIs
description: Authoritative delivery guarantees, ordering rules, and administrative API boundaries.
---

## Guarantees at a glance

| Area | Guarantee | Application responsibility |
| --- | --- | --- |
| Append | A batch of events commits atomically with stream versions and tenant offsets. | Supply the correct expected version and preserve a command's append ID when retrying an ambiguous outcome. |
| Aggregate repository | A successful non-idempotent save clears pending events. | Keep aggregate state changes inside `Apply` methods. |
| Tenant order | Events have consecutive, committed `TenantOffset` values per tenant. | Scope checkpoints and background reads to one tenant. |
| Transactional EF projection | Handler changes and its checkpoint commit atomically. | Use the supplied `EventStoreDbContext`; do not call `SaveChangesAsync` in the handler. |
| Asynchronous projection | At-least-once delivery. | Make every handler idempotent. |
| Outbox publication | Event and message commit together; external publication is at least once. | Deduplicate at the destination with `OutboxMessage.MessageId`. |
| Snapshot | Snapshot failures never modify committed event history; unusable snapshots fall back to replay. | Keep aggregate snapshot methods and upcasters deterministic. |

## Ordering and identity

- `StreamVersion` orders events inside one aggregate stream.
- `TenantOffset` orders committed events inside one tenant and is the position
  for projections and tenant-log readers.
- An event ID is UUIDv7. It is a stable event identity, but not the ordering
  mechanism.
- `AppendId` is a caller-owned idempotency key scoped to a tenant. Reuse it
  only for a retry of the same command.
- A projection identity is its `ProjectionKey` (`Name` and positive `Version`).
  Increment the version for a new read-model contract.

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
projection version that writes to a new or shadow table, let it catch up,
validate it, then switch readers.

## Outbox administration

`OutboxAdministration.GetAsync(tenantId, messageId)` reads one retained
message. `ReadAttemptsAsync(tenantId, messageId)` reads its delivery history.
Successful messages and attempts are deleted immediately by default; configure
a positive `SuccessfulDeliveryRetention` to make them inspectable. Pending and
failed messages are never automatically removed.

Keep these APIs behind an authenticated, audited application boundary. The
ASP.NET Core `MapEventLoomAdminDiagnostics` endpoint intentionally exposes only
aggregate health and schema counts; it does not expose tenant IDs, messages,
event payloads, metadata, or repair actions.
