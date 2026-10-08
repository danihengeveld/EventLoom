---
title: Use MongoDB
description: Run EventLoom on MongoDB with replica-set transactions, provider-transactional projections, and schema validation.
---

`EventLoom.MongoDb` is EventLoom's direct MongoDB provider. It uses
`MongoDB.Driver` transactions rather than EF Core, while application code still
uses the provider-neutral APIs from `EventLoom.Storage`.

## Install the provider

```bash
dotnet add package EventLoom.AspNetCore --version 0.1.0-alpha.0
dotnet add package EventLoom.MongoDb --version 0.1.0-alpha.0
```

Application code that injects `EventStore`, `AggregateRepository<TAggregate,
TId>`, `ProjectionAdministration`, or `OutboxAdministration` uses
`using EventLoom.Storage;`.

## Replica set or `mongos` is required

EventLoom MongoDB storage always uses multi-document transactions so appends,
tenant offsets, outbox writes, and transactional projection checkpoints commit
atomically. That requires either:

- a replica set, including a single-node development replica set; or
- a sharded deployment behind `mongos`.

A standalone `mongod` is incompatible. `IStorageSchema.ValidateAsync()` reports
that as `CanConnect = true`, `IncompatibleCount = 1`, and `IsCompatible = false`.

## Local development setup

Start a single-node replica set. A replica set needs one explicit initiation, and
`mongod` must accept connections first, so wait for it before calling
`rs.initiate`:

```bash
docker run -d --name eventloom-mongo -p 27017:27017 mongo:8 --replSet rs0 --bind_ip_all

until docker exec eventloom-mongo mongosh --quiet --eval "db.adminCommand('ping').ok" >/dev/null 2>&1; do
  sleep 1
done

docker exec eventloom-mongo mongosh --quiet --eval \
  "rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]})"

until docker exec eventloom-mongo mongosh --quiet --eval "db.hello().isWritablePrimary" | grep -q true; do
  sleep 1
done
```

The final loop exits when the node is `PRIMARY`; transactions fail until then.
Use this connection string. `directConnection=true` is required because the
replica-set member advertises `localhost:27017`:

```text
mongodb://localhost:27017/?directConnection=true
```

Prefer a compose file for a repeatable setup. The health check initiates the
replica set on first start and reports healthy only once the node is `PRIMARY`:

```yaml
services:
  mongo:
    image: mongo:8
    command: ["--replSet", "rs0", "--bind_ip_all"]
    ports:
      - "27017:27017"
    volumes:
      - mongo-data:/data/db
    healthcheck:
      test: >
        mongosh --quiet --eval "try { rs.status() } catch (e) {
        rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]}) }
        if (!db.hello().isWritablePrimary) quit(1)"
      interval: 5s
      timeout: 10s
      retries: 10
      start_period: 5s

volumes:
  mongo-data:
```

Run `docker compose up -d --wait`. In a .NET Aspire AppHost, use
`AddMongoDB("mongo").WithReplicaSet()` instead (an experimental API that needs
`#pragma warning disable ASPIREMONGODB001`); the sample AppHost shows it.

## Quick start

This complete console program creates the schema, saves an aggregate, and
reloads it. It uses the same aggregate and repository APIs as the
[first aggregate guide](/getting-started/first-aggregate).

```csharp
using EventLoom;
using EventLoom.Hosting;
using EventLoom.MongoDb;
using EventLoom.Storage;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services
    .AddEventLoom()
    .UseMongoDb("mongodb://localhost:27017/?directConnection=true", "eventloom")
    .AddAggregate<Counter, Guid>(aggregate => aggregate
        .ConstructWith(id => new Counter(id))
        .UseStream("counter", id => id.ToString("D")));

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();

await scope.ServiceProvider.GetRequiredService<IStorageSchema>().EnsureCreatedAsync();

var repository = scope.ServiceProvider.GetRequiredService<AggregateRepository<Counter, Guid>>();
var counter = new Counter(Guid.NewGuid());
counter.Increment(3);
await repository.SaveAsync(counter);

var reloaded = await repository.LoadAsync(counter.Id);
Console.WriteLine(reloaded.Value); // 3

public sealed record CounterIncremented(int Amount) : IDomainEvent<CounterIncremented, Counter>
{
    public static string EventType => "counter.incremented";
}

public sealed class Counter(Guid id) : Aggregate<Counter, Guid>(id), IApply<CounterIncremented>
{
    public int Value { get; private set; }

    public void Increment(int amount) => Raise(new CounterIncremented(amount));

    void IApply<CounterIncremented>.Apply(CounterIncremented @event) => Value += @event.Amount;
}
```

In an ASP.NET Core application, call
`await app.InitializeEventLoomDevelopmentDatabaseAsync()` inside an
`IsDevelopment()` check instead of resolving `IStorageSchema` yourself; it works
for MongoDB exactly as it does for PostgreSQL and SQLite and refuses to run
outside Development. Production deployments call `EnsureCreatedAsync()` and
`ValidateAsync()` from a reviewed deployment step.

## Register MongoDB storage

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

The alternate overload accepts a singleton client factory:

```csharp
eventLoom.UseMongoDb(
    services => services.GetRequiredService<IMongoClient>(),
    databaseName: "eventloom");
```

## Connect to Atlas, secured, or custom clusters

Everything in the connection string is passed to the official driver unchanged,
so use the driver's standard options:

```csharp
eventLoom.UseMongoDb(
    "mongodb+srv://app-user:{password}@cluster0.example.mongodb.net/?retryWrites=true&w=majority&readPreference=primary",
    "eventloom");
```

- **MongoDB Atlas.** Every Atlas cluster, including the free tier, is a replica
  set, so it satisfies the transaction requirement. Use the `mongodb+srv://`
  string from Atlas and allow your application's IP address in the Atlas network
  access list.
- **Credentials.** Keep them out of source control: read the connection string
  from user secrets, environment variables, or a secret store. URL-encode special
  characters in the password. If the user is defined in a database other than
  `admin`, add `authSource=<database>`.
- **TLS.** `mongodb+srv://` enables TLS automatically. For `mongodb://` add
  `tls=true`, and `tlsCAFile=<path>` for a private CA. Do not use
  `tlsAllowInvalidCertificates` outside local experiments.
- **Read preference.** Keep `readPreference=primary` (the default). EventLoom
  appends, offsets, and checkpoints rely on read-your-writes and snapshot
  transactions; reading from secondaries can return stale event history.
- **Write concern.** EventLoom transactions request `majority` themselves. Keep
  `retryWrites=true` (the default).
- **Replica-set members.** List several hosts, or use `+srv`, so the driver can
  discover the primary: `mongodb://host1,host2,host3/?replicaSet=rs0`.

When you need full control of the driver, build the client yourself with
`MongoClientSettings` and pass the factory overload. The client must be
long-lived: EventLoom registers it as a singleton.

```csharp
eventLoom.UseMongoDb(
    services =>
    {
        var settings = MongoClientSettings.FromConnectionString(
            services.GetRequiredService<IConfiguration>().GetConnectionString("EventLoom"));
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        settings.ApplicationName = "orders-api";
        return new MongoClient(settings);
    },
    databaseName: "eventloom");
```

Use `IsCompatible` from `IStorageSchema.ValidateAsync()` (or the EventLoom health
check) to confirm the deployment is a replica set or `mongos` before serving
traffic.

## Configure MongoDB options

`UseMongoDb(..., configure)` exposes `MongoDbStorageOptions`:

```csharp
eventLoom.UseMongoDb(connectionString, "eventloom", options =>
{
    options.CollectionPrefix = "app_";
    options.TransactionTimeout = TimeSpan.FromSeconds(45);
});
```

| `MongoDbStorageOptions` property | Default | Notes |
| --- | --- | --- |
| `CollectionPrefix` | `eventloom_` | Prefix for every EventLoom collection. Keep stable after data exists. |
| `TransactionTimeout` | 30 seconds | Maximum transaction commit time EventLoom requests from MongoDB. |
| `RegisterStandardGuidSerializer` | `true` | Registers `GuidSerializer(GuidRepresentation.Standard)` process-wide so your own documents can hold `Guid` values. See [Guid fields in your documents](#guid-fields-in-your-documents). |

The provider creates collections for streams, events, offsets, snapshots,
projection checkpoints, projection failures, projection leases, the outbox, and
outbox attempts.

## Guid fields in your documents

`MongoDB.Driver` 3 refuses to serialize a `Guid` until a representation is
configured, and neither `uuidRepresentation=standard` in the connection string
nor `MongoClientSettings` changes that. EventLoom's own documents are not
affected because they set the representation explicitly, but your projection
read models, inline projection documents, and unit-of-work writes would fail with
`GuidSerializer cannot serialize a Guid when GuidRepresentation is Unspecified`.

`UseMongoDb` therefore registers `GuidSerializer(GuidRepresentation.Standard)`
for the process by default. Registration is best effort: it is skipped when a
different `Guid` serializer is already registered, so call `UseMongoDb` (or your
own registration) before any code serializes a `Guid`, which in practice means
during startup. If your application registers its own `Guid` serializer after
`UseMongoDb`, or must keep the legacy representation, opt out:

```csharp
eventLoom.UseMongoDb(connectionString, "eventloom", options =>
    options.RegisterStandardGuidSerializer = false);

BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.CSharpLegacy));
```

The serializer is process-wide because the driver has no per-client
serialization settings. To avoid global state entirely, use string identifiers
in your documents (`id.ToString("D")`, as the sample does) or annotate
individual members with `[BsonGuidRepresentation(GuidRepresentation.Standard)]`.

## Build transactional projections

Transactional MongoDB projections implement `IMongoProjectionHandler<TEvent>`.
The handler receives a `MongoProjectionTransaction` with the shared session and
database.

```csharp
using EventLoom.Hosting;
using EventLoom.MongoDb;
using MongoDB.Driver;

public sealed class OrderSummaryProjection : IMongoProjectionHandler<OrderPlaced>
{
    public Task HandleAsync(
        EventEnvelope<OrderPlaced> envelope,
        MongoProjectionTransaction transaction,
        CancellationToken cancellationToken) =>
        transaction.Database
            .GetCollection<OrderSummaryDocument>("order_summaries")
            .ReplaceOneAsync(
                transaction.Session,
                value => value.Id == $"{envelope.TenantId!.Value}:{envelope.StreamId}",
                new OrderSummaryDocument
                {
                    Id = $"{envelope.TenantId!.Value}:{envelope.StreamId}",
                    TenantId = envelope.TenantId.Value,
                    OrderId = envelope.StreamId,
                    Status = "active",
                    TotalQuantity = envelope.Event.Quantity,
                    TenantOffset = envelope.TenantOffset
                },
                new ReplaceOptions { IsUpsert = true },
                cancellationToken);
}

builder.Services
    .AddEventLoom()
    .UseMongoDb(connectionString, "eventloom")
    .AddProjection("orders.summary", projection => projection
        .Transactional<OrderSummaryProjection, OrderPlaced>());
```

Keep MongoDB-specific projection registration in files that import
`EventLoom.MongoDb`. If the same file also imports
`EventLoom.EntityFrameworkCore`, the two provider-specific
`Transactional<,>()` extensions are ambiguous.

## Use inline projections

Inline projections can access the current append session through
`MongoSessionAccessor`:

```csharp
public sealed class InlineOrderCounter(MongoSessionAccessor mongo)
    : IInlineProjectionHandler<OrderPlaced>
{
    public Task HandleAsync(EventEnvelope<OrderPlaced> envelope, CancellationToken cancellationToken)
    {
        if (mongo.Session is null)
        {
            throw new InvalidOperationException("Inline projections run only inside an append session.");
        }

        return mongo.Database
            .GetCollection<OrderCounterDocument>("order_counters")
            .InsertOneAsync(
                mongo.Session,
                new OrderCounterDocument { Id = envelope.StreamId },
                cancellationToken: cancellationToken);
    }
}
```

Use inline projections only for transactional MongoDB work that must commit or
roll back with the append. Do not call external services from an inline
projection.

## Share a unit of work session

`EventStore.BeginUnitOfWorkAsync()` returns an EventLoom unit of work backed by
one MongoDB session. The MongoDB extension exposes that session:

```csharp
using EventLoom.Storage;
using EventLoom.MongoDb;
using MongoDB.Driver;

await using var unitOfWork = await eventStore.BeginUnitOfWorkAsync(cancellationToken);
var orders = mongoClient.GetDatabase("eventloom").GetCollection<OrderDocument>("orders");

await orders.InsertOneAsync(
    unitOfWork.Session,
    new OrderDocument { Id = order.Id.ToString("D") },
    cancellationToken: cancellationToken);

await unitOfWork.AppendAsync(new AppendRequest(
    TenantId: "acme",
    StreamId: order.Id.ToString("D"),
    AggregateType: "order",
    ExpectedVersion: ExpectedVersion.NoStream,
    Events: [new OrderPlaced("coffee", 2)],
    Metadata: new EventMetadata(Actor: "api")),
    cancellationToken);

await unitOfWork.CommitAsync(cancellationToken);
```

If `CommitAsync` is not called, disposing the unit of work aborts the MongoDB
transaction.

## Create and validate the schema

The MongoDB provider uses `IStorageSchema` like the relational providers:

```csharp
await using var scope = app.Services.CreateAsyncScope();
var schema = scope.ServiceProvider.GetRequiredService<IStorageSchema>();

await schema.EnsureCreatedAsync();
var validation = await schema.ValidateAsync();
```

`EnsureCreatedAsync()` creates the collections and indexes EventLoom needs. It
never migrates existing data. `ValidateAsync()` checks connectivity plus the
expected collections, indexes, and replica-set compatibility.

## Verify the setup

Use this checklist after bootstrapping MongoDB storage:

1. `await schema.ValidateAsync()` returns `IsCompatible = true`.
2. Append and reload an aggregate through `AggregateRepository<TAggregate, TId>`.
3. If you register transactional projections, confirm the read model changes and
   checkpoint advance together.
4. If you use units of work, verify your own MongoDB writes roll back when the
   EventLoom append fails.
5. Run `dotnet run --project tests/EventLoom.MongoDb.IntegrationTests -c Release --no-build`.

## Recovery and operational notes

- If validation reports `CanConnect = false`, fix connectivity or credentials
  first.
- If validation reports `IncompatibleCount > 0`, make sure you are connected to
  a replica set or `mongos`, not a standalone server.
- If validation reports `MissingCount > 0`, run `EnsureCreatedAsync()` against
  the target database, then validate again.
- Projection and outbox workers use fenced leases just like the relational
  providers. Releasing a lease expires it; the fencing token remains monotonic.

## Differences from PostgreSQL and SQLite

See the [feature gap reference](/reference/packages/#mongodb-and-ef-core-provider-feature-gap) for the complete list, including schema management, transaction limits, and test hosts.

| Concern | MongoDB | PostgreSQL / SQLite |
| --- | --- | --- |
| Provider implementation | Direct `MongoDB.Driver` | EF Core provider |
| Bootstrap model | Collections and indexes via `IStorageSchema` | EF Core context plus provider schema support |
| Transactional projection handler | `IMongoProjectionHandler<TEvent>` | `IEfProjectionHandler<TEvent>` |
| Inline projection access | `MongoSessionAccessor` | `EventStoreDbContext` |
| Shared unit of work surface | `unitOfWork.Session` | `unitOfWork.EnlistAsync(dbContext)` and `unitOfWork.DbTransaction` |
| Distributed support | Yes, replica set or `mongos` required | PostgreSQL yes; SQLite no |
