---
title: Packages and compatibility
description: Choose the EventLoom packages and storage provider that match your application topology.
---

EventLoom `0.1.0-alpha.0` targets .NET 10. The EF-based relational packages use
EF Core 10. All packages below are available from
[NuGet](https://www.nuget.org/profiles/danihengeveld).

## Package map

| Package | Reference directly when... | Provides |
| --- | --- | --- |
| `EventLoom` | Your domain project defines aggregates and events. | Domain contracts, aggregate dispatch, event metadata, serialization, IDs, expected versions, and bundled analyzer diagnostics. |
| `EventLoom.Storage` | Application or infrastructure code uses `EventStore`, `AggregateRepository<TAggregate, TId>`, `EventLoomUnitOfWork`, projection administration, or outbox administration directly. | The provider-neutral storage engine and storage SPI contracts. |
| `EventLoom.Hosting` | You need the DI/worker composition surface without ASP.NET Core helpers. | `AddEventLoom`, tenancy, projections, outbox, workers, health checks, and storage-provider registration. |
| `EventLoom.AspNetCore` | You are building an ASP.NET Core application. | Development schema initialization plus health and diagnostics endpoint helpers. |
| `EventLoom.EntityFrameworkCore` | You are building EF-specific infrastructure or transactional projection handlers. | Shared EF Core provider implementation, `EventStoreDbContext`, `ConfigureEntityFramework`, `ConfigureProjectionModel`, and `IEfProjectionHandler<TEvent>`. |
| `EventLoom.EntityFrameworkCore.PostgreSql` | The application runs multiple instances or distributed workers on PostgreSQL. | PostgreSQL provider selection, schema support, and retry classification. |
| `EventLoom.EntityFrameworkCore.Sqlite` | The application is local, embedded, test-only, or one controlled process. | SQLite provider selection and bundled native SQLite initialization. |
| `EventLoom.MongoDb` | The application stores EventLoom state in MongoDB. | Direct `MongoDB.Driver` provider, `UseMongoDb`, `IMongoProjectionHandler<TEvent>`, `MongoSessionAccessor`, and MongoDB unit-of-work access. |
| `EventLoom.Testing` | A test project exercises aggregates or SQLite-backed integration paths. | Aggregate scenarios, deterministic time/IDs, and a managed SQLite test host. |

Most applications reference `EventLoom.AspNetCore` plus exactly one provider.
The provider package brings in Hosting and Storage transitively. The analyzer is
bundled in the `EventLoom` package; `EventLoom.Analyzers` is not a separate
package for consumers.

## Provider capability matrix

| Capability | SQLite | PostgreSQL | MongoDB |
| --- | --- | --- | --- |
| Implementation | EF Core provider | EF Core provider | Direct `MongoDB.Driver` provider |
| Local development | Yes | Yes | Yes |
| Embedded / one controlled process | Yes | Yes | Usually overkill |
| Multiple application instances | No | Yes | Yes |
| Distributed projection or outbox workers | No | Yes | Yes |
| Transactional projections | Yes | Yes | Yes |
| Inline projections | Yes | Yes | Yes |
| Shared unit of work | Yes | Yes | Yes |
| Named schemas | No | Yes | N/A |
| Production recommendation | Local/test only | Relational production | MongoDB production |

Additional provider notes:

- **SQLite** is intentionally single-node only.
- **PostgreSQL** is the production relational provider.
- **MongoDB** requires a replica set or `mongos` because EventLoom always uses
  multi-document transactions.

## MongoDB and EF Core provider feature gap

The storage contract, capabilities, and guarantees are identical: every provider
passes the same conformance suite. The differences below are in tooling,
extension points, and operations, so review them before choosing MongoDB or
moving an application between providers.

| Area | EF Core providers (PostgreSQL, SQLite) | MongoDB |
| --- | --- | --- |
| Topology | PostgreSQL: any server. SQLite: in-process file or memory. | Replica set or `mongos` only; a standalone `mongod` is reported incompatible. There is no in-process option. |
| Schema management | Tables and columns; hosts own reviewed EF Core migrations. `ValidateAsync` reports missing tables, missing columns, and incompatible columns. | `EnsureCreatedAsync` creates collections and named indexes; there are no migrations. `ValidateAsync` checks that the collections and EventLoom's named indexes exist, not their definitions or document shape. |
| Schema customization | `ConfigureEntityFramework` (`Schema`, `TablePrefix`), `ConfigureDbContext`, `ConfigureProjectionModel`. | `MongoDbStorageOptions.CollectionPrefix`, `TransactionTimeout`, and `RegisterStandardGuidSerializer` only; there are no named schemas. |
| Transactional projection handlers | `IEfProjectionHandler<TEvent>` with `EventStoreDbContext`; read models are mapped into the EventLoom context. | `IMongoProjectionHandler<TEvent>` with `MongoProjectionTransaction` (session and database). Handlers are not portable between providers. |
| Inline projections and unit of work | `unitOfWork.EnlistAsync(dbContext)` and `unitOfWork.DbTransaction` coordinate application `DbContext` instances. | `unitOfWork.Session` and `MongoSessionAccessor`; application writes must pass the session explicitly. There is no automatic enlistment. |
| Transaction limits | Database transaction limits. | MongoDB transaction limits (lifetime, size, and oplog constraints) apply. `TransactionTimeout` bounds the commit time only, and very large append batches can hit server limits. |
| Time values | Native `DateTimeOffset` columns. | Stored as UTC tick `Int64` values, so native MongoDB tooling shows ticks rather than dates. |
| Ad hoc inspection | SQL over the event tables. | MongoDB queries over the `events` collection. |
| Managed test host | `EventLoom.Testing` provides `EventLoomSqliteTestHost`. | None. Use Testcontainers with `WithReplicaSet()` as `EventLoom.MongoDb.IntegrationTests` does. |

If you depend on EF Core features such as migrations, a shared `DbContext`
transaction with your own entities, or SQL reporting over event tables, choose a
relational provider. If you do not, the programming model in your aggregates,
repositories, projections registration, and outbox publishers is the same.

## Package references

An ASP.NET Core service normally references `EventLoom.AspNetCore` and one
provider:

```xml
<ItemGroup>
  <PackageReference Include="EventLoom.AspNetCore" Version="0.1.0-alpha.0" />
  <PackageReference Include="EventLoom.MongoDb" Version="0.1.0-alpha.0" />
</ItemGroup>
```

Substitute one of these provider packages as needed:

- `EventLoom.EntityFrameworkCore.PostgreSql`
- `EventLoom.EntityFrameworkCore.Sqlite`
- `EventLoom.MongoDb`

If your code injects `EventStore` or `AggregateRepository<TAggregate, TId>`,
import `EventLoom.Storage` in the consuming source file.
