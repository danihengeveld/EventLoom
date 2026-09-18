---
title: Projections
description: Build recoverable asynchronous read models with checkpoints, leases, failure recovery, and explicit inline projections.
---

An EventLoom projection consumes the tenant-ordered event log to build a read
model. Asynchronous projections are the default. Their delivery guarantee is
**at least once**: an external side effect can repeat if a worker stops after
the handler succeeds but before its checkpoint commits.

For database-only read models, use an EF projection handler. EventLoom runs it
and advances the checkpoint in one database transaction, producing
effectively-once database effects.

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
        // Use envelope.Event and operational metadata such as TenantOffset.
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

The worker is registered automatically when the first asynchronous projection
is registered. It reads each tenant's event log in strict `TenantOffset`
order, acquires a tenant/projection/version lease, and advances the checkpoint
after every event. A projection checkpoint also advances past event types that
the projection does not handle.

Use this handler form for external effects only when the destination is
idempotent. EventLoom will retry bounded handler failures and can redeliver
after a process interruption. Prefer an outbox for external integration once
that feature is available.

## Build an EF read model atomically

Map a read model into the dedicated EventLoom context, then implement
`IEfProjectionHandler<TEvent>`. The provided context participates in the
handler-and-checkpoint transaction. Add or update tracked entities but do not
call `SaveChangesAsync`; EventLoom commits the read-model changes and
checkpoint together.

```csharp
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

`ConfigureProjectionModel` is for read models owned by the projection. Keep
the mappings explicit and use normal reviewed host migrations in production.

## Inline projections

Inline projections are deliberately separate from normal projections. They run
before an append transaction commits and can veto that append by throwing:

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

eventLoom.AddProjection("orders.counter", projection => projection
    .Inline<InlineOrderCounter, OrderPlaced>());
```

An inline handler resolves the same scoped `EventStoreDbContext` as the
append. It must do only transactional database work; never call HTTP services,
publish messages, or perform other effects that cannot be rolled back.

The named API makes the durable checkpoint identity explicit once. Pass
`version: 2` when changing a projection's read-model contract:

```csharp
eventLoom.AddProjection("orders.summary", projection => projection
    .Transactional<OrderSummaryV2Projection, OrderPlaced>(), version: 2);
```

`Asynchronous`, `Transactional`, and `Inline` describe the delivery behavior
at the registration site. Legacy `AddProjection`, `AddEfProjection`, and
`AddInlineProjection` overloads remain supported for existing applications.

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

// Fix the cause first, then retry the failed event.
await administration.ResumeAsync("acme", key);

// Intentionally discard one failed event and continue.
await administration.SkipAsync("acme", key, failedEventId);

// Reprocess this projection version from tenant offset zero.
await administration.ReplayAsync("acme", key);
```

`SkipAsync` is intentionally explicit because it creates a gap in that read
model. `ReplayAsync` changes only the checkpoint; it never clears a read
model. For a safe production rebuild, deploy a new projection version that
writes a new or shadow table, let its independent checkpoint catch up from
zero, validate it, then switch readers to the new table.

## Worker configuration and providers

Configure polling, batches, leases, and retry count with
`ConfigureWorkers`. PostgreSQL supports multi-instance projection workers and
fences stale owners with lease tokens checked inside every projection commit.
SQLite is suitable for local and controlled single-node use only. Do not run
multiple SQLite application instances against the same projection store.
