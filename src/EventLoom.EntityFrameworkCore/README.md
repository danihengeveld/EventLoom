# EventLoom.EntityFrameworkCore

`EventLoom.EntityFrameworkCore` is EventLoom's EF Core storage provider. It
implements the `EventLoom.Storage` provider contract (events, snapshots,
projections, outbox, and worker leases) over the dedicated
`EventStoreDbContext`. The provider-neutral `EventStore`,
`AggregateRepository<,>`, and unit-of-work API (`EventStore.BeginUnitOfWorkAsync`)
live in `EventLoom.Storage`; this package adds the EF-specific parts:

- `ConfigureEntityFramework(options => ...)` for the schema and table prefix;
- `ConfigureProjectionModel` and `ConfigureDbContext` for read models and
  context customization;
- `IEfProjectionHandler<TEvent>` for projections that commit EF read-model
  changes in the same transaction as their checkpoint;
- `unitOfWork.EnlistAsync(dbContext)` and `unitOfWork.DbTransaction` for
  coordinating an append with application database changes.

Storage logging comes from the provider-neutral engine and uses standard .NET
logging; logs omit persisted identifiers, payloads, and exception messages.

Snapshots are aggregate-owned state caches. Declare a snapshot as
`IAggregateSnapshot<TSnapshot, TAggregate>`, implement
`ISnapshotable<TSnapshot>` on the aggregate, and configure it with
`UseSnapshots<TSnapshot>(...)`.

It is intentionally a dependency package rather than a normal application
entry point. Install one provider package instead:

- `EventLoom.EntityFrameworkCore.PostgreSql` for production deployments with
  multiple instances or distributed workers;
- `EventLoom.EntityFrameworkCore.Sqlite` for local development, tests,
  embedded applications, and one controlled process.

To store events in MongoDB, use `EventLoom.MongoDb` instead; it does not use
EF Core.

Web applications should install `EventLoom.AspNetCore` plus one provider; the
provider brings this package into the dependency graph. Reference this package
directly only when building specialized infrastructure around
`EventStoreDbContext`.

EventLoom is currently pre-release. See the
[EF Core configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md)
for provider setup and the
[projection guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/projections.md)
for delivery and recovery semantics.

Projection tenant discovery and health lag use the persisted tenant-offset
counters, not an event-table scan. A tenant counter at zero does not represent
committed projection work.
