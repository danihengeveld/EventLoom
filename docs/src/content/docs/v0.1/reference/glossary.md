---
slug: v0.1/reference/glossary
title: Glossary
description: Definitions for the EventLoom domain, persistence, and delivery terminology used throughout this documentation.
---

## Domain and persistence

| Term | Meaning |
| --- | --- |
| **Aggregate** | An application-owned consistency boundary that changes state only by applying domain events. |
| **Domain event** | An immutable application-owned fact that implements `IDomainEvent<TAggregate>` and has a stable `[EventType]` name and version. |
| **Event envelope** | The stored event plus persistence metadata: tenant, stream, versions, event ID, occurrence time, and metadata. |
| **Stream** | The ordered event history for one aggregate identity and aggregate type within a tenant. |
| **Stream version** | A consecutive number that orders events within one stream and supports optimistic concurrency. |
| **Expected version** | The stream state an append requires: no stream, an exact version, an existing stream, or any version. |
| **Append ID** | A caller-owned, tenant-scoped identifier used to safely retry the same command after an ambiguous failure. |
| **Tenant offset** | A consecutive, committed position that orders all events for one tenant. It is the checkpoint position for asynchronous processing. |
| **Snapshot** | A versioned aggregate-owned state cache, declared as `IAggregateSnapshot<TAggregate>`, that shortens replay. Event history remains authoritative. |

## Processing and delivery

| Term | Meaning |
| --- | --- |
| **Projection** | Code that consumes committed events to produce a read model or perform a side effect. |
| **Projection key** | A projection's durable identity: a stable name and positive version. Each version has independent checkpoints and failures. |
| **Checkpoint** | The tenant offset through which a projection has durably completed processing. |
| **Transactional EF projection** | A projection whose EventLoom-context read-model changes and checkpoint commit in one transaction. |
| **Asynchronous projection** | A background projection that is delivered at least once and must therefore be idempotent. |
| **Inline projection** | A handler that runs inside the event append transaction and can veto the append. It may perform transactional database work only. |
| **Outbox message** | A durable integration message written atomically with an event append when an outbox publisher is configured. |
| **Outbox publisher** | A background worker that delivers pending outbox messages. Delivery is at least once. |
| **Lease** | Time-limited worker ownership for a tenant and processing responsibility. PostgreSQL uses fenced leases to reject stale worker commits. |

## Operational outcomes

| Term | Meaning |
| --- | --- |
| **At least once** | A handler or publisher can receive the same event or message more than once. Effects must be idempotent. |
| **Effectively once** | Transactional EF projection changes and their checkpoint commit together, preventing duplicate database effects at that boundary. |
| **Idempotency** | Repeating an operation yields the same externally visible result as running it once. |
| **Paused projection** | A projection version that stopped for a tenant after exhausting retry attempts. An operator must resume, skip, or rebuild it. |
| **Replay** | Resetting a projection checkpoint so a projection version processes tenant history again. Replay does not clear a read model. |
