---
title: Append and read events
description: Persist and read tenant-scoped event streams with EventLoom.
---

Use `EventStore` for bounded append and read operations. The API does not expose
`IQueryable`, so callers cannot accidentally bypass tenant and ordering
boundaries.

## Configure the store

Register an `EventRegistry`, `EventSerializer`, and the provider-specific
`EventStoreDbContext` through dependency injection:

```csharp
services.AddSingleton(new EventRegistry()
    .RegisterEvent<ProductAdded>());
services.AddSingleton<EventSerializer>();
services.AddSingleton<IEventIdGenerator, UuidV7EventIdGenerator>();
services.AddSingleton(TimeProvider.System);
services.AddScoped<EventStore>();
```

The event store context is configured separately from the application context.
See [Configure the EF Core event store](/guides/configure-ef-core) for provider
configuration and migrations.

## Append a batch

An append is atomic. Stream versions and tenant global positions are assigned
consecutively inside the transaction:

```csharp
var result = await store.AppendAsync(new AppendRequest(
    TenantId: "acme",
    StreamId: "cart-123",
    AggregateType: "shopping-cart",
    ExpectedVersion: ExpectedVersion.NoStream,
    Events: [new ProductAdded("sku-1", 2)],
    Metadata: new EventMetadata(Actor: "checkout"),
    AppendId: "checkout-command-456"),
    cancellationToken);
```

Use `AppendId` when retrying a command after an ambiguous network failure. A
successful retry returns the original persisted envelopes and marks
`WasIdempotentReplay` as `true`.

## Read a stream

```csharp
var history = await store.ReadStreamAsync(
    tenantId: "acme",
    streamId: "cart-123",
    cancellationToken: cancellationToken);
```

Use `fromVersion` and `toVersion` for a bounded stream range, or
`ReadPositionsAsync` to consume a tenant's committed global order in batches.
Every returned `EventEnvelope` contains stream identity, tenant, event identity,
stream version, global position, timestamp, and operational metadata.

SQLite supports this API for local and single-node use. Use PostgreSQL when
multiple application instances or distributed workers are required.
