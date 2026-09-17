---
title: Outbox and application integration
description: Reliably publish integration messages after an EventLoom append.
---

Every committed EventLoom event creates one durable outbox message in the same
database transaction. The message ID is the event's UUIDv7 event ID. Register
one transport-neutral publisher to deliver those messages:

```csharp
builder.Services.AddEventLoom(eventLoom => eventLoom
    .RegisterEvent<OrderPlaced>()
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("EventStore")!)
    .AddOutboxPublisher<OrderIntegrationPublisher>());
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

## Delivery guarantee and idempotency

Outbox delivery is **at least once**. A process can publish a message
successfully and stop before EventLoom records its success. EventLoom will then
deliver the same message again. A publisher must make external effects
idempotent using `OutboxMessage.MessageId`.

EventLoom persists every completed delivery attempt, including whether it
succeeded and the exception type for failures. It deliberately does not store
exception messages or payload copies in attempt history. Failed messages remain
unpublished and are retried with bounded exponential backoff during the
current worker iteration, then again on later polls. There is no general
exactly-once guarantee across EventLoom and an external broker, HTTP service,
or email provider.

`OutboxMessage` contains the persisted event identity, tenant, stream,
aggregate type, stream version, tenant offset, event type and version,
occurred time, JSON payload, and metadata. Treat its payload and metadata as
application data: log neither by default, and authenticate/authorize any
administrative inspection endpoint that exposes them.

## Inspect a delivery

Use explicit tenant scope for administrative reads:

```csharp
var message = await outboxAdministration.GetAsync("acme", messageId);
var attempts = await outboxAdministration.ReadAttemptsAsync("acme", messageId);
```

The Ordering API sample registers a logging publisher and exposes
`GET /outbox/{messageId}` for its required request tenant. An event's
`eventId` from `GET /orders/{id}/events` is the corresponding outbox message
ID.

## Share one application transaction

Normal appends commit independently. When application state and EventLoom
events must commit or roll back together in the same relational database, use
the advanced shared-connection path. Both contexts must receive the **same
scoped `DbConnection` instance**, then the caller owns the transaction:

```csharp
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

builder.Services.AddScoped<DbConnection>(_ =>
    new NpgsqlConnection(builder.Configuration.GetConnectionString("App")!));

builder.Services.AddDbContext<OrderDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<DbConnection>()));

builder.Services.AddEventLoom(eventLoom => eventLoom
    .RegisterEvent<OrderPlaced>()
    .UsePostgreSql(services => services.GetRequiredService<DbConnection>()));
```

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
Do not use this path for separate databases, different connections, or
distributed transactions; use the normal append plus outbox delivery instead.
