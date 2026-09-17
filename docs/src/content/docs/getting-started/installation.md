---
title: Installation
description: Prepare an EventLoom development checkout.
---

EventLoom targets .NET 10 and the EF Core event store is split into a core package plus a provider package.

Install the package for the database provider used by your application:

```bash
dotnet add package EventLoom.EntityFrameworkCore
dotnet add package EventLoom.EntityFrameworkCore.Sqlite
# Or use EventLoom.EntityFrameworkCore.PostgreSql for PostgreSQL.
```

The SQLite and PostgreSQL packages provide provider capability metadata; the context and migrations live in the core package.

To work on the repository, install the SDK selected by `global.json`, then run:

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
dotnet run --project tests/EventLoom.EntityFrameworkCore.UnitTests
dotnet run --project tests/EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests
dotnet run --project tests/EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests
pnpm --dir docs build
```