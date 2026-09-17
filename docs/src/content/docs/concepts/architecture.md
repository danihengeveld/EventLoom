---
title: Architecture
description: The architectural boundaries agreed for EventLoom.
---

EventLoom uses a dedicated `EventStoreDbContext`, separate from an application's EF Core context. Both can use the same database, while the event store retains clear transaction, migration, and performance boundaries.

PostgreSQL is the distributed production provider. SQLite supports development and single-node scenarios. Events are immutable records with stable type metadata, while stream identity and operational metadata live in persisted envelopes.

Phase 3 introduces the EF Core storage boundary:

- `EventStoreDbContext` owns the event-store model and is kept separate from an application's context.
- `EventStoreOptions` controls the table prefix and optional database schema.
- `EventStoreSchema.MigrateAsync` applies the event-store migrations, while `GetTableNames` exposes the tables for diagnostics.
- Provider capability objects make schema and distributed-worker support explicit. PostgreSQL supports both; SQLite does not support schemas or distributed workers.

See the architecture decisions in the repository for the complete rationale.