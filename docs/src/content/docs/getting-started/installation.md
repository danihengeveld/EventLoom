---
title: Installation
description: Install EventLoom from NuGet and choose the appropriate storage provider.
---

## Status and prerequisites

EventLoom `0.1.0-alpha.0` targets **.NET 10** and is available from NuGet.
Because it is pre-release, APIs and persistence contracts may change before
the first stable release.

You need:

- the .NET SDK specified by the repository's `global.json`;
- PostgreSQL 17+ for distributed and multi-instance scenarios, or SQLite for
  local and controlled single-node scenarios;
- Docker when running PostgreSQL Testcontainers tests or the Aspire-hosted
  Ordering API sample;
- pnpm only when building the documentation site.

## Install EventLoom

Install the ASP.NET Core application package and exactly one provider. For
PostgreSQL:

```bash
dotnet add package EventLoom.AspNetCore --version 0.1.0-alpha.0
dotnet add package EventLoom.EntityFrameworkCore.PostgreSql --version 0.1.0-alpha.0
dotnet add package EventLoom.Analyzers --version 0.1.0-alpha.0
```

For SQLite:

```bash
dotnet add package EventLoom.AspNetCore --version 0.1.0-alpha.0
dotnet add package EventLoom.EntityFrameworkCore.Sqlite --version 0.1.0-alpha.0
dotnet add package EventLoom.Analyzers --version 0.1.0-alpha.0
```

The provider projects bring in the provider-neutral EF Core event store. Choose
one provider for an application. The [Ordering API sample](/guides/ordering-api)
uses PostgreSQL through .NET Aspire to demonstrate the production path.

## Choose a provider

| Scenario | Provider | Notes |
| --- | --- | --- |
| Local development, tests, embedded app, one controlled process | SQLite | No schemas or distributed workers. |
| Production with multiple application instances or workers | PostgreSQL | Distributed correctness and worker leases are supported. |

PostgreSQL is the production provider. SQLite is intentionally not a substitute
for PostgreSQL concurrency testing or a distributed deployment.

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
pnpm --dir docs build
```

The Testcontainers environment variable is needed only on Docker Desktop
installations where the Ryuk resource-reaper image cannot run. Do not set it
globally without arranging normal container cleanup.

Next, [build your first aggregate](/getting-started/first-aggregate).
