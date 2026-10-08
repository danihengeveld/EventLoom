---
title: Publish integration messages
description: Reliably publish integration messages after an EventLoom append.
---

When an outbox publisher is registered, every committed EventLoom event creates
one durable outbox message in the same storage transaction. EventLoom does not
create outbox messages when no publisher is configured. The message ID is the
event's UUIDv7 event ID. Register one transport-neutral publisher to deliver
those messages:

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")))
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
order and uses the fenced lease name `outbox:publisher`. PostgreSQL and MongoDB
support multiple application instances; SQLite is appropriate only for one
controlled instance.

Successfully published messages and their attempt history are deleted
immediately by default. `AddOutboxPublisher` groups every outbox worker
control—instance identity, polling interval, batch size, lease duration and
renewal, retry attempts, and successful-delivery retention—behind one cohesive
options callback:

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")))
    .AddOutboxPublisher<OrderIntegrationPublisher>(options =>
    {
        options.SuccessfulDeliveryRetention = TimeSpan.FromDays(7);
        options.PollInterval = TimeSpan.FromSeconds(1);
        options.BatchSize = 100;
        options.MaxRetryAttempts = 5;
    });
```

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
positive retention period is configured.

## Inspect a delivery

Configure a positive successful-delivery retention period, then use explicit
tenant scope for administrative reads:

```csharp
var message = await outboxAdministration.GetAsync("acme", messageId);
var attempts = await outboxAdministration.ReadAttemptsAsync("acme", messageId);
```

## Share one application transaction

Normal appends commit independently. When application state and EventLoom
events must commit or roll back together, use `EventStore.BeginUnitOfWorkAsync()`.

### EF Core providers

Both contexts must first receive the **same scoped `DbConnection` instance**:

```csharp
using System.Data.Common;
using EventLoom.EntityFrameworkCore;
using EventLoom.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

builder.Services.AddScoped<DbConnection>(_ =>
    new NpgsqlConnection(builder.Configuration.GetConnectionString("App")!));

builder.Services.AddDbContext<OrderDbContext>((services, options) =>
    options.UseNpgsql(services.GetRequiredService<DbConnection>()));

builder.Services
    .AddEventLoom()
    .UsePostgreSql(services => services.GetRequiredService<DbConnection>())
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
```

Then enlist the application context into the shared transaction:

```csharp
await using var unitOfWork = await eventStore.BeginUnitOfWorkAsync(cancellationToken);
await unitOfWork.EnlistAsync(orderDbContext, cancellationToken);

orderDbContext.Orders.Add(order);
await orderDbContext.SaveChangesAsync(cancellationToken);
await unitOfWork.AppendAsync(append, cancellationToken);

await unitOfWork.CommitAsync(cancellationToken);
```

### MongoDB provider

MongoDB exposes the shared `IClientSessionHandle` through `unitOfWork.Session`:

```csharp
using EventLoom.MongoDb;
using EventLoom.Storage;
using MongoDB.Driver;

await using var unitOfWork = await eventStore.BeginUnitOfWorkAsync(cancellationToken);
var orders = mongoClient.GetDatabase("eventloom").GetCollection<OrderDocument>("orders");

await orders.InsertOneAsync(unitOfWork.Session, new OrderDocument { Id = order.Id.ToString("D") }, cancellationToken: cancellationToken);
await unitOfWork.AppendAsync(append, cancellationToken);
await unitOfWork.CommitAsync(cancellationToken);
```

If a shared transaction is not required, prefer the normal append path plus the
transactional outbox.
