---
title: Tenancy and ordering
description: Configure tenant isolation, idempotent command retries, expected versions, and global positions.
---

## Tenancy modes

`TenancyMode.Disabled` is the default. Use it only when the application has no
tenant boundary. For a tenant-aware application, require a scoped accessor:

```csharp
services.AddScoped<ITenantAccessor, RequestTenantAccessor>();
services.AddEventLoom(eventLoom => eventLoom
    .ConfigureTenancy(TenancyMode.Required)
    .RegisterEvent<OrderPlaced>()
    .UsePostgreSql(connectionString));
```

When required, EventLoom normalizes tenant IDs and rejects operations with no
tenant or an explicit tenant that differs from the scoped accessor. Keep tenant
resolution at the trusted application boundary, such as authentication
middleware. Never use an unvalidated client header as a production trust
decision; the sample uses `X-Tenant-ID` only to make the mechanism observable.

Short repository operations resolve the scoped tenant:

```csharp
await repository.SaveAsync(order);
var order = await repository.LoadAsync(orderId);
```

Administrative or background code can use the explicit repository overloads
and must supply the intended tenant:

```csharp
await repository.SaveAsync(
    tenantId: "acme",
    streamId: orderId.ToString("D"),
    aggregateType: "order",
    aggregate: order,
    metadata: new EventMetadata(Actor: "maintenance"));
```

## Expected versions and concurrency

Every append uses an expected version:

| Expectation | Meaning |
| --- | --- |
| `ExpectedVersion.NoStream` | Create only when the stream does not exist. |
| `ExpectedVersion.Exact(version)` | Append only when the stream is at that version. |
| `ExpectedVersion.StreamExists` | Append only to an existing stream. |
| `ExpectedVersion.Any` | Do not perform an application-level version check. |

The aggregate repository derives the exact expectation from the aggregate's
replayed version. With PostgreSQL, concurrent conflicts are surfaced as
`WrongExpectedVersionException` or `EventStoreConcurrencyException`; handlers
should reload, reevaluate the command, and retry only when that is valid for
the business operation.

## Append IDs

An `AppendId` identifies one caller-owned command attempt across a tenant. Use
the exact same ID when retrying after an ambiguous timeout or connection
failure:

```csharp
await repository.SaveAsync(
    order,
    appendId: commandId);
```

If the original append committed, the retry returns the persisted envelopes
with `WasIdempotentReplay` set to `true`. EventLoom does not invent command
identity: generating a new append ID on each retry defeats idempotency.

## Authoritative read order

Every event has:

- a **stream version**, which orders facts within one aggregate stream;
- a **per-tenant global position**, which orders committed events for a tenant.

Use `ReadStreamAsync` to rebuild one aggregate and `ReadPositionsAsync` to
read a bounded tenant sequence. Event IDs are UUIDv7 for locality and
diagnostics, but they are not the authoritative ordering mechanism. PostgreSQL
serializes tenant position allocation transactionally so a reader cannot skip a
late commit by advancing its checkpoint.
