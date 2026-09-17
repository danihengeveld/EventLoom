---
title: Architecture
description: The architectural boundaries agreed for EventLoom.
---

EventLoom uses a dedicated `EventStoreDbContext`, separate from an application's EF Core context. Both can use the same database, while the event store retains clear transaction, migration, and performance boundaries.

PostgreSQL is the distributed production provider. SQLite supports development and single-node scenarios. Events are immutable records with stable type metadata, while stream identity and operational metadata live in persisted envelopes.

See the architecture decisions in the repository for the complete rationale.