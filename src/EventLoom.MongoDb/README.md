# EventLoom.MongoDb

`EventLoom.MongoDb` is EventLoom's direct MongoDB storage provider. It uses
`MongoDB.Driver` transactions rather than EF Core and persists events,
snapshots, projection checkpoints, outbox messages, and worker leases in one
MongoDB database.

Install it as the application entry point for MongoDB-backed EventLoom
persistence:

```bash
dotnet add package EventLoom.MongoDb --prerelease
```

Register it through the hosting API:

```csharp
using EventLoom;
using EventLoom.Hosting;
using EventLoom.MongoDb;

builder.Services
    .AddEventLoom()
    .UseMongoDb(
        "mongodb://localhost:27017/?directConnection=true",
        "eventloom")
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
```

## Replica set or mongos required

EventLoom MongoDB storage always uses multi-document transactions to keep
stream appends, tenant offsets, outbox rows, and transactional projection
checkpoints atomic. MongoDB therefore must be configured as either:

- a replica set, including single-node development replica sets; or
- a sharded deployment behind `mongos`.

A standalone `mongod` is incompatible and `IStorageSchema.ValidateAsync()`
reports it as incompatible.

## Options

`UseMongoDb(..., configure)` exposes `MongoDbStorageOptions`:

- `CollectionPrefix` — prefixes every EventLoom collection name; defaults to
  `eventloom_`.
- `TransactionTimeout` — sets the maximum transaction commit time; defaults to
  30 seconds.
- `RegisterStandardGuidSerializer` — registers the driver's standard `Guid`
  serializer process-wide so your own documents can hold `Guid` values (the
  driver otherwise refuses them); defaults to `true`, best effort, and should be
  set to `false` if your application registers its own `Guid` serializer.

For Atlas, credentials, TLS, read preference, and a local replica set, see the
[Use MongoDB guide](https://eventloom.hengeveld.dev/guides/use-mongodb/).

## Collections and indexes

The provider creates these collections on first use or when
`IStorageSchema.EnsureCreatedAsync()` is called:

- `streams`
- `events`
- `offsets`
- `snapshots`
- `projection_checkpoints`
- `projection_failures`
- `projection_leases`
- `outbox`
- `outbox_attempts`

Event documents enforce unique tenant stream versions, unique tenant offsets,
tenant-scoped event ids, and a tenant-scoped append id index used for idempotent replay. Projection and outbox
collections create the indexes needed for ordered reads, checkpoint lookup,
failure resolution, and fenced leases.

EventLoom stores timestamps as UTC tick `Int64` values instead of BSON dates so
append, snapshot invalidation, outbox retention, and checkpoint comparisons keep
full .NET `DateTimeOffset` precision.

## Transactional projections and inline projections

Transactional projections can write MongoDB read models in the same transaction
as the checkpoint by implementing `IMongoProjectionHandler<TEvent>` and
registering them with `.Transactional<THandler, TEvent>()`.

Inline projections can inject `MongoSessionAccessor` to access the current
`IClientSessionHandle` and the EventLoom `IMongoDatabase` during an append.
When no inline append is running, `MongoSessionAccessor.Session` is `null`.

## Unit of work

`EventStore.BeginUnitOfWorkAsync()` returns an EventLoom unit of work backed by
one MongoDB session. `EventLoomUnitOfWorkMongoDbExtensions.Session` exposes the
shared `IClientSessionHandle` so application code can participate in the same
transaction.

## Limitations

- MongoDB retryable transaction semantics depend on replica-set or sharded
  deployments; standalone servers are unsupported.
- The provider stores EventLoom payloads exactly as the serializer's JSON
  strings; it does not project event payload fields into MongoDB indexes.
- Read models written by inline or transactional projections must use the
  supplied session when they need to commit atomically with EventLoom state.
