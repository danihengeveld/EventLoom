# EventLoom.EntityFrameworkCore

`EventLoom.EntityFrameworkCore` is EventLoom's shared EF Core infrastructure
package. It provides the dedicated `EventStoreDbContext`, transactional
appends, aggregate repositories, snapshots, checkpointed projections, the
transactional outbox model, and a unit-of-work API
(`EventStore.BeginUnitOfWorkAsync`) for coordinating an append with
application database changes in the same transaction.

The EF Core package uses standard .NET logging for unexpected append
failures, rejected appends, snapshot replay fallback, and successful
projection administration changes. Host registration supplies the logger
automatically; applications control providers and levels normally. Direct
construction without a logger remains supported and uses a no-op logger.
Logs omit persisted identifiers, payloads, and exception messages.

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

EventLoom is currently pre-release. See the
[EF Core configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md)
for provider setup and the
[projection guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/projections.md)
for delivery and recovery semantics.

Projection tenant discovery and health lag use the persisted tenant-offset
counters, not an event-table scan. A tenant counter at zero does not represent
committed projection work.
