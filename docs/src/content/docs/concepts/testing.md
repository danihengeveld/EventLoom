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
  provider tests. PostgreSQL integration tests that require a server run against
  PostgreSQL Testcontainers. Provider capability tests remain runnable on every
  developer machine.

The test projects deliberately do not share a broad application test assembly.
This makes package references, provider assumptions, and CI failures explicit.

SQLite is not used to make distributed-worker or PostgreSQL concurrency claims.
Those guarantees require PostgreSQL integration and stress tests.

The PostgreSQL integration suite requires Docker. On Docker Desktop
installations that block the Testcontainers resource-reaper image, run the
suite with `TESTCONTAINERS_RYUK_DISABLED=true` and clean up containers after the
run using the Docker Desktop environment's normal lifecycle controls.
