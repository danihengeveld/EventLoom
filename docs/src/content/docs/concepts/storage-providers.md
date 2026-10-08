---
title: Storage providers
description: Understand EventLoom's storage SPI, the built-in provider matrix, and how to choose or build a provider.
---

EventLoom is split into two layers:

- **engine**: `EventLoom.Storage` exposes the APIs application code uses,
  including `EventStore`, `AggregateRepository<TAggregate, TId>`,
  `EventLoomUnitOfWork`, projection administration, and outbox administration;
- **provider**: a package such as `EventLoom.EntityFrameworkCore.PostgreSql`,
  `EventLoom.EntityFrameworkCore.Sqlite`, or `EventLoom.MongoDb` implements the
  storage SPI and registers itself through `EventLoomBuilder.UseStorage(...)`.

That split keeps aggregates and normal command-handling code stable even when
persistence technology changes.

## What every provider must guarantee

A storage provider is not just a serializer and a few tables or collections. It
must preserve the EventLoom contract:

1. every append is atomic;
2. `ExpectedVersion` is enforced per stream;
3. tenant offsets are gapless, monotonic per tenant, and become visible in
   commit order;
4. every read and write is tenant-scoped;
5. worker leases use monotonically increasing fencing tokens and verify them
   when projections or the outbox record progress;
6. `WrongExpectedVersionException` and `EventStoreConcurrencyException` remain
   visible as logical conflicts.

The shared conformance suite in `tests/EventLoom.Storage.Conformance` runs these
rules against every built-in provider.

## Built-in providers

| Provider | Implementation | Transactional projections | Inline projections | Unit of work | Distributed deployment | Recommended use |
| --- | --- | --- | --- | --- | --- | --- |
| PostgreSQL | EF Core provider | Yes | Yes | Yes | Yes | Production relational deployments |
| MongoDB | Direct `MongoDB.Driver` provider | Yes | Yes | Yes | Yes, replica set or `mongos` required | Production document-store deployments |
| SQLite | EF Core provider | Yes | Yes | Yes | No | Local development, embedded apps, tests, controlled single-node use |

### Provider-specific notes

- **PostgreSQL** is the production relational provider. It supports named
  schemas and multi-instance workers.
- **MongoDB** stores EventLoom state directly in MongoDB collections. It always
  uses multi-document transactions, so a standalone `mongod` is incompatible.
- **SQLite** implements the same programming model but is intentionally
  single-node only. Do not use it to prove distributed correctness.

## How to choose a provider

Choose based on topology first, then data-model preference:

- Choose **SQLite** when one controlled process owns the database and low-friction
  local setup matters most.
- Choose **PostgreSQL** when you want relational tooling, SQL access, EF Core
  projection models, or host-owned migrations.
- Choose **MongoDB** when your application already centers on MongoDB and you
  want EventLoom to participate in MongoDB transactions directly instead of
  through EF Core.

## EF Core vs direct-driver providers

### EF Core providers

**Benefits**

- familiar relational migrations and schema review;
- `ConfigureProjectionModel(...)` for transactional read-model mappings;
- easy coordination with application `DbContext` instances through
  `unitOfWork.EnlistAsync(...)`.

**Trade-offs**

- storage behavior is constrained by relational database semantics;
- provider-specific tuning lives in EF Core provider options;
- read models that must commit atomically with checkpoints must be mapped into
  the EventLoom `EventStoreDbContext`.

### Direct MongoDB provider

**Benefits**

- no EF Core layer between EventLoom and MongoDB;
- transactional projections and units of work use native MongoDB sessions;
- document storage can model read models directly in collections.

**Trade-offs**

- multi-document transactions require a replica set or `mongos`;
- provider-specific projection code uses `MongoProjectionTransaction` or
  `MongoSessionAccessor` instead of `DbContext`;
- MongoDB bootstrap is index and collection creation, not EF migrations.

The [feature gap reference](/reference/packages/#mongodb-and-ef-core-provider-feature-gap) lists every behavioral and tooling difference between the providers.

## Writing a custom provider

A custom provider implements the SPI from `EventLoom.Storage` and registers its
services through `EventLoomBuilder.UseStorage(...)` in `EventLoom.Hosting`.
That registration must provide scoped implementations of:

- `IEventStorage`
- `ISnapshotStorage`
- `IProjectionStorage`
- `IOutboxStorage`
- `IWorkerLeaseStorage`
- `IStorageSchema`

Start from the `EventLoom.Storage` README in the repository and run the shared
conformance suite against your provider before documenting it as supported.
Capabilities such as transactional projections, inline projections, shared
units of work, and distributed deployment are declared through
`StorageCapabilities` and validated by Hosting.
