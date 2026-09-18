---
title: Outbox and application integration
description: Reliably publish integration messages after an EventLoom append.
---

When an outbox publisher is registered, every committed EventLoom event creates
one durable outbox message in the same database transaction. EventLoom does not
create outbox messages when no publisher is configured. The message ID is the
event's UUIDv7 event ID. Register one transport-neutral publisher to deliver
those messages:

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddEvent<OrderPlaced>()
    .AddOutboxPublisher<OrderIntegrationPublisher>();
```

```csharp
public sealed class OrderIntegrationPublisher : IOutboxPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        // Send message.Payload and message.Metadata to a broker or HTTP endpoint.
        // Supply message.MessageId as the destination's idempotency key.
        throw new NotImplementedException();
    }
}
```

Register exactly one publisher. Its registration starts the hosted outbox
worker. The worker reads each tenant's unpublished messages in tenant-offset
order and uses a fenced, tenant-scoped lease. PostgreSQL supports multiple
application instances; SQLite is appropriate only for one controlled instance.

Successfully published messages and their attempt history are deleted
immediately by default. `AddOutboxPublisher` groups every outbox worker
control&mdash;instance identity, polling interval, batch size, lease duration
and renewal, retry attempts, and successful-delivery retention&mdash;behind
one cohesive options callback:

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddEvent<OrderPlaced>()
    .AddOutboxPublisher<OrderIntegrationPublisher>(options =>
    {
        options.SuccessfulDeliveryRetention = TimeSpan.FromDays(7);
        options.PollInterval = TimeSpan.FromSeconds(1);
        options.BatchSize = 100;
        options.MaxRetryAttempts = 5;
    });
```

Cleanup runs in bounded batches and removes only successfully published
messages older than the configured duration. Pending and failed messages are
never removed automatically. Unlike `ConfigureWorkers`, which only tunes the
projection worker, these outbox settings apply solely to the outbox worker
started by `AddOutboxPublisher`.

## Delivery guarantee and idempotency

Outbox delivery is **at least once**. A process can publish a message
successfully and stop before EventLoom records its success. EventLoom will then
deliver the same message again. A publisher must make external effects
idempotent using `OutboxMessage.MessageId`.

While a message exists, EventLoom persists every completed delivery attempt,
including whether it succeeded and the exception type for failures. It
deliberately does not store exception messages or payload copies in attempt
history. Failed messages remain unpublished and are retried with bounded
exponential backoff during the current worker iteration, then again on later
polls. A successful message and its attempts remain inspectable only when a
positive retention period is configured. There is no general exactly-once
guarantee across EventLoom and an external broker, HTTP service, or email
provider.

`OutboxMessage` contains the persisted event identity, tenant, stream,
aggregate type, stream version, tenant offset, event type and version,
occurred time, JSON payload, and metadata. Treat its payload and metadata as
application data: log neither by default, and authenticate/authorize any
administrative inspection endpoint that exposes them.

## Inspect a delivery

Configure a positive successful-delivery retention period, then use explicit
tenant scope for administrative reads:

```csharp
var message = await outboxAdministration.GetAsync("acme", messageId);
var attempts = await outboxAdministration.ReadAttemptsAsync("acme", messageId);
```

The Ordering API sample registers a logging publisher that retains successful
deliveries for one day, and exposes
`GET /outbox/{messageId}` for its required request tenant. An event's
`eventId` from `GET /orders/{id}/events` is the corresponding outbox message
ID.

## Share one application transaction

Normal appends commit independently. When application state and EventLoom
events must commit or roll back together in the same relational database,
both contexts must first receive the **same scoped `DbConnection` instance**:

```csharp
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

builder.Services.AddScoped<DbConnection>(_ =>
    new NpgsqlConnection(builder.Configuration.GetConnectionString("App")!));

builder.Services.AddDbContext<OrderDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<DbConnection>()));

builder.Services
    .AddEventLoom()
    .UsePostgreSql(services => services.GetRequiredService<DbConnection>())
    .AddEvent<OrderPlaced>();
```

Then use `EventStore.BeginUnitOfWorkAsync` to coordinate the append and the
application context's `SaveChangesAsync` without directly managing a
`DbTransaction`:

```csharp
await using var unitOfWork = await eventStore.BeginUnitOfWorkAsync(cancellationToken);
await unitOfWork.EnlistAsync(orderDbContext, cancellationToken);

orderDbContext.Orders.Add(order);
await orderDbContext.SaveChangesAsync(cancellationToken);
await unitOfWork.AppendAsync(append, cancellationToken);

await unitOfWork.CommitAsync(cancellationToken);
```

`EnlistAsync` verifies that the application context shares the exact
connection used by EventLoom before attaching it to the unit of work's
transaction. Call `CommitAsync` or `RollbackAsync` exactly once; disposing the
unit of work without committing rolls the transaction back. Do not use this
path for separate databases, different connections, or distributed
transactions; use the normal append plus outbox delivery instead.

### Advanced: managing the transaction directly

Callers that already own a `DbTransaction`, for example inside existing
transactional infrastructure, can bypass the unit of work and enlist EventLoom
directly:

```csharp
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
await orderDbContext.Database.UseTransactionAsync(transaction, cancellationToken);

orderDbContext.Orders.Add(order);
await orderDbContext.SaveChangesAsync(cancellationToken);
await eventStore.AppendInTransactionAsync(append, transaction, cancellationToken);

await transaction.CommitAsync(cancellationToken);
```

`AppendInTransactionAsync` verifies that the supplied transaction belongs to
the exact connection used by EventLoom, and never commits or rolls it back.
