---
title: Packages and compatibility
description: Choose the EventLoom packages and storage provider that match your application topology.
---

EventLoom `0.1.0-alpha.0` targets .NET 10 and EF Core 10. All packages below
are available from [NuGet](https://www.nuget.org/profiles/danihengeveld).
EventLoom remains pre-release, so APIs and persistence contracts may change
before the first stable release.

## Choose an application entry point

| Package | Reference directly when... | Provides |
| --- | --- | --- |
| `EventLoom` | Your domain project defines aggregates and events. | Domain contracts, aggregate dispatch, event metadata, serialization, IDs, and expected versions. |
| `EventLoom.AspNetCore` | You are building an ASP.NET Core application. | Development schema initialization and health/diagnostic endpoint helpers. |
| `EventLoom.EntityFrameworkCore.PostgreSql` | The application runs multiple instances or distributed workers. | PostgreSQL provider configuration, transient retry classification, and distributed lease support. |
| `EventLoom.EntityFrameworkCore.Sqlite` | The application is local, embedded, test-only, or one controlled process. | SQLite provider configuration and bundled native SQLite initialization. |
| `EventLoom.Testing` | A test project exercises aggregates or SQLite-backed integration paths. | Aggregate scenarios, event fixtures, deterministic time/IDs, and a managed SQLite test host. |
| `EventLoom.Analyzers` | A project declares persisted events, snapshots, or aggregates. | Compile-time validation of persisted identities, event ownership, `Apply` methods, and snapshot methods. |

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

## Package references

An ASP.NET Core service references `EventLoom.AspNetCore`, one provider, and
the analyzer:

```xml
<ItemGroup>
  <PackageReference Include="EventLoom.AspNetCore" Version="0.1.0-alpha.0" />
  <PackageReference Include="EventLoom.EntityFrameworkCore.PostgreSql" Version="0.1.0-alpha.0" />
  <PackageReference Include="EventLoom.Analyzers" Version="0.1.0-alpha.0"
                    PrivateAssets="all" />
</ItemGroup>
```

Substitute `EventLoom.EntityFrameworkCore.Sqlite` only for the SQLite
scenarios described above. See [Installation](/getting-started/installation).
