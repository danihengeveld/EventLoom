---
title: Packages and compatibility
description: Choose the EventLoom packages and storage provider that match your application topology.
---

EventLoom targets .NET 10 and EF Core 10. The projects below define the
intended package surface. They are currently evaluated from this repository;
they are not yet available from NuGet.

## Choose an application entry point

| Package | Reference directly when... | Provides |
| --- | --- | --- |
| `EventLoom` | Your domain project defines aggregates and events. | Domain contracts, aggregate dispatch, event metadata, serialization, IDs, and expected versions. |
| `EventLoom.AspNetCore` | You are building an ASP.NET Core application. | Development schema initialization and health/diagnostic endpoint helpers. |
| `EventLoom.EntityFrameworkCore.PostgreSql` | The application runs multiple instances or distributed workers. | PostgreSQL provider configuration, transient retry classification, and distributed lease support. |
| `EventLoom.EntityFrameworkCore.Sqlite` | The application is local, embedded, test-only, or one controlled process. | SQLite provider configuration and bundled native SQLite initialization. |
| `EventLoom.Testing` | A test project exercises aggregates or SQLite-backed integration paths. | Aggregate scenarios, event fixtures, deterministic time/IDs, and a managed SQLite test host. |
| `EventLoom.Analyzers` | A project declares persisted events and aggregates. | Compile-time validation of event ownership and `Apply` methods. |

`EventLoom.EntityFrameworkCore` and `EventLoom.Hosting` are usually transitive
dependencies. Reference them directly only when building custom infrastructure
around the event store or a non-web host.

## Choose exactly one provider

| Requirement | SQLite | PostgreSQL |
| --- | --- | --- |
| Local development and tests | Yes | Yes |
| Embedded or one controlled process | Yes | Yes |
| Multiple application instances | No | Yes |
| Distributed projection or outbox workers | No | Yes |
| Database schemas | No | Yes |
| Production service with independent worker scaling | No | Yes |

SQLite implements the normal append/read APIs but deliberately does not provide
distributed-worker correctness. Do not use it to validate a multi-instance
deployment. PostgreSQL is the provider for production systems that need
concurrent instances, lease fencing, and authoritative per-tenant ordering.

## Package references from a checkout

An ASP.NET Core service references `EventLoom.AspNetCore` and one provider:

```xml
<ItemGroup>
  <ProjectReference Include="../EventLoom/src/EventLoom.AspNetCore/EventLoom.AspNetCore.csproj" />
  <ProjectReference Include="../EventLoom/src/EventLoom.EntityFrameworkCore.PostgreSql/EventLoom.EntityFrameworkCore.PostgreSql.csproj" />
  <ProjectReference Include="../EventLoom/src/EventLoom.Analyzers/EventLoom.Analyzers.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Substitute `EventLoom.EntityFrameworkCore.Sqlite` only for the SQLite
scenarios described above. See [Installation](/getting-started/installation)
for prerequisites and repository validation commands.
