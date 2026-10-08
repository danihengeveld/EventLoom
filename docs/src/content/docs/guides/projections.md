---
title: Build projections
description: Build recoverable asynchronous read models with checkpoints, leases, failure recovery, and provider-specific transactional handlers.
---

An EventLoom projection consumes the tenant-ordered event log to build a read
model. Asynchronous projections are the default. Their delivery guarantee is
**at least once**: an external side effect can repeat if a worker stops after
the handler succeeds but before its checkpoint commits.

For database-only read models, use a provider-transactional projection handler.
EventLoom runs it and advances the checkpoint in one provider transaction,
producing effectively-once read-model effects.

## Register an asynchronous projection

Implement one typed handler interface for each event a logical projection
handles. Register them through one named projection so every handler shares the
same stable name and version:

```csharp
public sealed class OrderSummaryProjection :
    IProjectionHandler<OrderPlaced>,
    IProjectionHandler<OrderCancelled>
{
    public Task HandleAsync(EventEnvelope<OrderPlaced> envelope, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task HandleAsync(EventEnvelope<OrderCancelled> envelope, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

eventLoom
    .AddProjection("orders.summary", projection => projection
        .Asynchronous<OrderSummaryProjection, OrderPlaced>()
        .Asynchronous<OrderSummaryProjection, OrderCancelled>());
```

The worker is registered automatically when the first asynchronous projection is
registered. It reads each tenant's event log in strict `TenantOffset` order,
acquires a tenant/projection/version lease, and advances the checkpoint after
every event. A projection checkpoint also advances past event types that the
projection does not handle.

Use this handler form for external effects only when the destination is
idempotent. EventLoom will retry bounded handler failures and can redeliver
after a process interruption.

## Build an EF read model atomically

Map a read model into the dedicated EventLoom context, then implement
`IEfProjectionHandler<TEvent>`. The supplied `EventStoreDbContext`
participates in the handler-and-checkpoint transaction. Add or update tracked
entities but do not call `SaveChangesAsync`; EventLoom commits the read-model
changes and checkpoint together.

```csharp
using EventLoom.EntityFrameworkCore;

eventLoom.ConfigureProjectionModel(modelBuilder =>
{
    modelBuilder.Entity<OrderSummary>(entity =>
    {
        entity.ToTable("order_summaries");
        entity.HasKey(value => new { value.TenantId, value.OrderId });
    });
});

public sealed class OrderSummaryProjection : IEfProjectionHandler<OrderPlaced>
{
    public Task HandleAsync(
        EventEnvelope<OrderPlaced> envelope,
        EventStoreDbContext context,
        CancellationToken cancellationToken)
    {
        context.Set<OrderSummary>().Add(new OrderSummary
        {
            TenantId = envelope.TenantId!.Value.Value,
            OrderId = Guid.Parse(envelope.StreamId),
            Status = "active"
        });
        return Task.CompletedTask;
    }
}

eventLoom.AddProjection("orders.summary", projection => projection
    .Transactional<OrderSummaryProjection, OrderPlaced>());
```

## Build a MongoDB read model atomically

MongoDB transactional projections implement `IMongoProjectionHandler<TEvent>`
and receive a `MongoProjectionTransaction`:

```csharp
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
                    TenantOffset = envelope.TenantOffset
                },
                new ReplaceOptions { IsUpsert = true },
                cancellationToken);
}

eventLoom.AddProjection("orders.summary", projection => projection
    .Transactional<OrderSummaryProjection, OrderPlaced>());
```

Keep EF and Mongo transactional registrations in separate files. If one file
imports both `EventLoom.EntityFrameworkCore` and `EventLoom.MongoDb`, the two
provider-specific `Transactional<,>()` extensions are ambiguous.

## Inline projections

Inline projections run before an append transaction commits and can veto that
append by throwing.

### EF Core inline projection

```csharp
public sealed class InlineOrderCounter(EventStoreDbContext context)
    : IInlineProjectionHandler<OrderPlaced>
{
    public Task HandleAsync(EventEnvelope<OrderPlaced> envelope, CancellationToken cancellationToken)
    {
        context.Set<OrderCounter>().Add(new OrderCounter { Count = 1 });
        return Task.CompletedTask;
    }
}
```

### MongoDB inline projection

```csharp
public sealed class InlineOrderCounter(MongoSessionAccessor mongo)
    : IInlineProjectionHandler<OrderPlaced>
{
    public Task HandleAsync(EventEnvelope<OrderPlaced> envelope, CancellationToken cancellationToken) =>
        mongo.Database
            .GetCollection<OrderCounterDocument>("order_counters")
            .InsertOneAsync(
                mongo.Session ?? throw new InvalidOperationException("No EventLoom append session is active."),
                new OrderCounterDocument { Id = envelope.StreamId },
                cancellationToken: cancellationToken);
}
```

Register inline handlers with `.Inline<THandler, TEvent>()`. Use them only for
transactional provider work; never call HTTP services, publish messages, or
perform other effects that cannot be rolled back.

## Failures, pause, resume, skip, and replay

EventLoom retries a failed delivery up to `MaxRetryAttempts`, then persists a
failure record with tenant, projection version, event ID/type, offset, attempt
count, timestamp, and exception type. Payload values are never copied into the
failure record. That projection version pauses for the affected tenant; other
projections and tenants continue.

Administration always requires an explicit tenant and projection key:

```csharp
var key = new ProjectionKey("orders.summary", Version: 1);

var checkpoint = await administration.GetCheckpointAsync("acme", key);
var failures = await administration.ReadFailuresAsync("acme", key);

await administration.ResumeAsync("acme", key);
await administration.SkipAsync("acme", key, failedEventId);
await administration.ReplayAsync("acme", key);
```

`SkipAsync` is intentionally explicit because it creates a gap in that read
model. `ReplayAsync` changes only the checkpoint; it never clears a read model.
For a safe production rebuild, deploy a new projection version that writes a new
or shadow table or collection, let its independent checkpoint catch up from
zero, validate it, then switch readers.

## Worker configuration and providers

Configure polling, batches, leases, and retry count with `ConfigureWorkers`.
PostgreSQL and MongoDB support multi-instance projection workers and fence stale
owners with lease tokens checked inside every checkpoint commit. SQLite is
suitable for local and controlled single-node use only.
