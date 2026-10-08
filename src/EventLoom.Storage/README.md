# EventLoom.Storage

The storage-agnostic heart of EventLoom: the `EventStore`, `AggregateRepository<TAggregate, TId>`, units of
work, snapshot handling, and the **provider contract** that every storage provider implements.

You normally install a provider package rather than this one directly:

| Provider                                   | Store                                      |
| ------------------------------------------ | ------------------------------------------ |
| `EventLoom.EntityFrameworkCore.PostgreSql` | PostgreSQL (production, distributed)       |
| `EventLoom.EntityFrameworkCore.Sqlite`     | SQLite (local, tests, single process)      |
| `EventLoom.MongoDb`                        | MongoDB replica set or sharded cluster     |

## What lives here

- `EventStore` appends and reads events with optimistic concurrency, tenant scoping, idempotent appends, and
  telemetry, regardless of the database behind it.
- `AggregateRepository<TAggregate, TId>` loads and saves aggregates, including snapshots.
- `EventLoomUnitOfWork` shares one provider transaction between an append and application state.
- The provider contract: `IEventStorage`, `ISnapshotStorage`, `IProjectionStorage`, `IOutboxStorage`,
  `IWorkerLeaseStorage`, `IStorageSchema`, and `StorageCapabilities`.

## Writing a storage provider

A provider implements the contract interfaces and registers them through `EventLoomBuilder.UseStorage` from
`EventLoom.Hosting`. Every provider must:

1. make each append atomic and enforce `ExpectedVersion` per stream;
2. assign gapless, monotonic per-tenant offsets that become visible in offset order;
3. scope every read and write to a tenant;
4. grant leases with monotonically increasing fencing tokens and verify them when a worker records progress;
5. make `ReleaseAsync` expire the lease rather than delete it, so fencing tokens never restart;
6. treat an `AppendId` as tenant-wide: appending it again returns the original events with `WasIdempotentReplay`;
7. throw `WrongExpectedVersionException` for a failed version expectation and `EventStoreConcurrencyException`
   for a lost write race it cannot retry.

The shared conformance suite in `tests/EventLoom.Storage.Conformance` exercises these guarantees and is run
against every built-in provider.
