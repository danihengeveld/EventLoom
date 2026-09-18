# EventLoom.EntityFrameworkCore

`EventLoom.EntityFrameworkCore` is EventLoom's shared EF Core infrastructure
package. It provides the dedicated `EventStoreDbContext`, transactional
appends, aggregate repositories, snapshots, checkpointed projections, the
transactional outbox model, and a unit-of-work API
(`EventStore.BeginUnitOfWorkAsync`) for coordinating an append with
application database changes in the same transaction.

Snapshots are aggregate-owned state caches. Declare a snapshot as
`IAggregateSnapshot<TAggregate>` and configure it with
`UseSnapshots<TSnapshot>(...)`; the aggregate supplies private
`CreateSnapshot()` and `RestoreSnapshot(TSnapshot)` methods.

It is intentionally a dependency package rather than a normal application
entry point. Install one provider package instead:

- `EventLoom.EntityFrameworkCore.PostgreSql` for production deployments with
  multiple instances or distributed workers;
- `EventLoom.EntityFrameworkCore.Sqlite` for local development, tests,
  embedded applications, and one controlled process.

Web applications should install `EventLoom.AspNetCore` plus one provider; the
provider brings this package into the dependency graph. Reference this package
directly only when building specialized infrastructure around
`EventStoreDbContext`.

Packages are currently pre-release and not published to NuGet. See the
[EF Core configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md)
for provider setup and the
[projection guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/projections.md)
for delivery and recovery semantics.
