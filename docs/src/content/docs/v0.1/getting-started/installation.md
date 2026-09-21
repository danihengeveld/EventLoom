---
slug: v0.1/getting-started/installation
title: Installation
description: Set up EventLoom from source and choose the appropriate storage provider.
---

## Status and prerequisites

EventLoom currently targets **.NET 10** and is pre-release. Its NuGet packages
are not published yet, so use a checkout when evaluating or contributing to
the library.

You need:

- the .NET SDK specified by the repository's `global.json`;
- PostgreSQL 17+ for distributed and multi-instance scenarios, or SQLite for
  local and controlled single-node scenarios;
- Docker when running PostgreSQL Testcontainers tests or the Aspire-hosted
  Ordering API sample;
- pnpm only when building the documentation site.

## Add EventLoom from this checkout

Reference the ASP.NET Core application package and one provider project:

```xml
<ItemGroup>
  <ProjectReference Include="../EventLoom/src/EventLoom.AspNetCore/EventLoom.AspNetCore.csproj" />
  <ProjectReference Include="../EventLoom/src/EventLoom.EntityFrameworkCore.PostgreSql/EventLoom.EntityFrameworkCore.PostgreSql.csproj" />
</ItemGroup>
```

For SQLite instead, reference:

```xml
<ProjectReference Include="../EventLoom/src/EventLoom.AspNetCore/EventLoom.AspNetCore.csproj" />
<ProjectReference Include="../EventLoom/src/EventLoom.EntityFrameworkCore.Sqlite/EventLoom.EntityFrameworkCore.Sqlite.csproj" />
```

The provider projects bring in the provider-neutral EF Core event store. Choose
one provider for an application. The [Ordering API sample](/v0.1/guides/ordering-api)
uses PostgreSQL through .NET Aspire to demonstrate the production path.

When EventLoom packages are published, use the corresponding
`EventLoom.AspNetCore` and one provider package from NuGet instead of project
references.

## Choose a provider

| Scenario | Provider | Notes |
| --- | --- | --- |
| Local development, tests, embedded app, one controlled process | SQLite | No schemas or distributed workers. |
| Production with multiple application instances or workers | PostgreSQL | Distributed correctness and worker leases are supported. |

PostgreSQL is the production provider. SQLite is intentionally not a substitute
for PostgreSQL concurrency testing or a distributed deployment.

## Verify the checkout

From the repository root:

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
dotnet run --project tests/EventLoom.EntityFrameworkCore.UnitTests
dotnet run --project tests/EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests
TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet run --project tests/EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests
pnpm --dir docs build
```

The Testcontainers environment variable is needed only on Docker Desktop
installations where the Ryuk resource-reaper image cannot run. Do not set it
globally without arranging normal container cleanup.

Next, [build your first aggregate](/v0.1/getting-started/first-aggregate).
