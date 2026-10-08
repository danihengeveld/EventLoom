---
title: Installation
description: Install EventLoom from NuGet and choose the storage provider that matches your topology.
---

## Status and prerequisites

EventLoom `0.1.0-alpha.0` targets **.NET 10** and is available from NuGet.
Because it is pre-release, APIs and persistence contracts may change before the
first stable release.

You need:

- the .NET SDK specified by the repository's `global.json`;
- one storage backend:
  - PostgreSQL 17+ for distributed relational deployments;
  - MongoDB 8+ configured as a replica set or behind `mongos` for distributed
    document-store deployments;
  - SQLite for local, embedded, test, or controlled single-process scenarios;
- Docker when running PostgreSQL or MongoDB Testcontainers tests, or the
  Aspire-hosted Ordering API sample;
- pnpm only when building the documentation site.

## Install EventLoom

Install the ASP.NET Core application package and exactly one provider.

### PostgreSQL

```bash
dotnet add package EventLoom.AspNetCore --version 0.1.0-alpha.0
dotnet add package EventLoom.EntityFrameworkCore.PostgreSql --version 0.1.0-alpha.0
```

### MongoDB

```bash
dotnet add package EventLoom.AspNetCore --version 0.1.0-alpha.0
dotnet add package EventLoom.MongoDb --version 0.1.0-alpha.0
```

MongoDB must run as a replica set. [Use MongoDB](/guides/use-mongodb) has a
local setup and a complete quick start.

### SQLite

```bash
dotnet add package EventLoom.AspNetCore --version 0.1.0-alpha.0
dotnet add package EventLoom.EntityFrameworkCore.Sqlite --version 0.1.0-alpha.0
```

The provider package brings in `EventLoom.Hosting` and `EventLoom.Storage`
transitively. Application code that injects `EventStore`,
`AggregateRepository<TAggregate, TId>`, `ProjectionAdministration`, or
`OutboxAdministration` uses the `EventLoom.Storage` namespace.

## Choose a provider

| Scenario | Provider | Notes |
| --- | --- | --- |
| Local development, tests, embedded app, one controlled process | SQLite | EF Core provider, no distributed correctness. |
| Distributed relational production deployment | PostgreSQL | EF Core provider, schemas supported, safe multi-instance workers. |
| Distributed document-store deployment | MongoDB | Direct `MongoDB.Driver` provider, requires replica set or `mongos`. |

All built-in providers support projections, snapshots, the outbox, and units of
work. Only PostgreSQL and MongoDB are supported for multi-instance deployment.
See [Storage providers](/concepts/storage-providers) for the full matrix.

## Build from source

Use a repository checkout when contributing to EventLoom itself. From the
repository root:

```bash
dotnet restore EventLoom.slnx --locked-mode
dotnet build EventLoom.slnx --configuration Release --no-restore
dotnet run --project tests/EventLoom.UnitTests --configuration Release --no-build
dotnet run --project tests/EventLoom.EntityFrameworkCore.UnitTests --configuration Release --no-build
dotnet run --project tests/EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests --configuration Release --no-build
TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet run --project tests/EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests --configuration Release --no-build
TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet run --project tests/EventLoom.MongoDb.IntegrationTests --configuration Release --no-build
pnpm --dir docs build
```

The Testcontainers environment variable is needed only on Docker Desktop setups
where the Ryuk resource-reaper image cannot run. Do not set it globally without
arranging normal container cleanup.

Next, [build your first aggregate](/getting-started/first-aggregate).
