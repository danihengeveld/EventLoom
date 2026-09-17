---
title: Architecture
description: The storage, domain, and provider boundaries used by EventLoom.
---

EventLoom separates domain behavior from event-store infrastructure.

```text
Application command
  -> Aggregate<TId> raises immutable events
  -> AggregateRepository or EventStore appends a batch
  -> EventStoreDbContext persists streams, events, and tenant offsets
  -> Application reads a stream or tenant position range
```

## Domain boundary

Your application owns aggregates and event types. An event payload contains
business facts only: stream identity, aggregate type, tenant, event ID,
versions, timestamps, correlation data, and tenant offsets belong to the immutable
event envelope. This prevents a domain event from being coupled to one
transport or persistence layout.

`Aggregate<TId>` uses cached, compiled dispatch to invoke typed private or
protected `Apply(TEvent)` methods. It is intentionally opinionated: state
changes flow through events, and events are replayed in stream order.

## Storage boundary

`EventStoreDbContext` is dedicated to EventLoom. Keep it separate from an
application `DbContext`, even when both use the same physical database. This
keeps migrations, transaction ownership, indexing, and operational tuning
independent.

Each append is atomic. It validates an expected version, assigns stream
versions and a per-tenant offset, writes all events, and commits or
rolls back as a unit. No `IQueryable` is exposed from `EventStore`; reads are
bounded by stream version or position.

## Provider boundary

- **PostgreSQL** is the distributed production provider. It supports schemas,
  serializable append transactions, per-tenant position allocation, retry
  classification, and worker-lease primitives.
- **SQLite** supports the same normal append/read API for local, embedded, and
  controlled single-node use. It does not support schemas or distributed
  workers.

Use the provider-specific composition extensions rather than configuring the
EventLoom context manually in most applications.

## Delivery boundary

Current EventLoom supports event persistence, aggregate reconstruction, and
optional snapshots. Projection runners and outbox publication are planned but
not implemented. Do not treat the current event store as a general
cross-context transaction or message-delivery mechanism. The repository's accepted
[architecture decisions](https://github.com/danihengeveld/EventLoom/tree/main/docs/architecture/decisions)
record the rationale.
