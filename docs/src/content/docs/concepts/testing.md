---
title: Testing strategy
description: How EventLoom separates domain, EF Core, SQLite, and PostgreSQL tests.
---

EventLoom keeps test ownership aligned with package ownership:

- `EventLoom.UnitTests` covers the EF-independent domain kernel, event registry,
  serialization, and upcasting.
- `EventLoom.EntityFrameworkCore.UnitTests` covers provider-neutral model and
  capability contracts.
- `EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests` uses a real in-memory
  SQLite database for relational schema, append, rollback, query, and aggregate
  repository behavior.
- `EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests` owns PostgreSQL
  provider tests. PostgreSQL integration tests that require a server are run
  against PostgreSQL Testcontainers in the integration pipeline; provider
  capability tests remain runnable on every developer machine.

The test projects deliberately do not share a broad application test assembly.
This makes package references, provider assumptions, and CI failures explicit.

SQLite is not used to make distributed-worker or PostgreSQL concurrency claims.
Those guarantees require PostgreSQL integration and stress tests.
