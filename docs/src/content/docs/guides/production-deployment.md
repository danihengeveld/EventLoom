---
title: Deploy and recover
description: Operate EventLoom safely in PostgreSQL or MongoDB deployments and keep recovery procedures explicit.
---

This guide covers the current distributed-production boundary for EventLoom.
Use PostgreSQL or MongoDB for multi-instance deployments. SQLite is intentionally
single-node only.

## Choose one distributed provider deliberately

Pick the provider before production data exists and keep its durable names
stable.

### PostgreSQL

```csharp
builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .UseMultiTenancy<AuthenticatedTenantAccessor>()
    .ConfigureEntityFramework(options =>
    {
        options.Schema = "eventloom";
        options.TablePrefix = "eventloom_";
    })
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
```

### MongoDB

```csharp
builder.Services
    .AddEventLoom()
    .UseMongoDb(
        builder.Configuration.GetConnectionString("EventStore")!,
        databaseName: "eventloom")
    .UseMultiTenancy<AuthenticatedTenantAccessor>()
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
```

Do not change event names, aggregate type names, stream-ID formats, collection
prefixes, schema names, or table prefixes after writing production data without
an explicit migration or data move.

## Deploy store structures deliberately

### PostgreSQL and SQLite

EventLoom ships the EF Core model but not EventLoom-owned migrations. Build and
review migrations for the dedicated `EventStoreDbContext` in your host
application, then apply them as a deployment step before rolling out
application instances.

Do not use `EnsureCreatedAsync` in a production database that is or will be
managed with migrations.

### MongoDB

MongoDB uses explicit bootstrap rather than migrations. Run `IStorageSchema`
creation and validation as part of environment provisioning:

```csharp
await using var scope = app.Services.CreateAsyncScope();
var schema = scope.ServiceProvider.GetRequiredService<IStorageSchema>();

await schema.EnsureCreatedAsync();
var validation = await schema.ValidateAsync();
```

`EnsureCreatedAsync()` creates the required collections and indexes only when
they do not exist. It never migrates existing data.

## Validate before serving traffic

For every provider, use `IStorageSchema.ValidateAsync()` as a read-only gate:

- `CanConnect` confirms the store is reachable;
- `MissingCount` reports missing tables, columns, collections, or indexes;
- `IncompatibleCount` reports incompatible objects;
- `IsCompatible` is true only when the store is reachable and complete.

For MongoDB, `IncompatibleCount > 0` typically means the server is not a replica
set member and not behind `mongos`.

## Retry and conflict behavior

PostgreSQL and MongoDB install bounded retry policies for transient
infrastructure failures. A retry reruns the full append transaction. Provide a
stable `AppendId` for any command that can be retried after an ambiguous
client-visible failure.

Expected-version and unique-constraint conflicts are business-visible. Reload
the aggregate, reassess the command, and decide whether a new command is
appropriate. Never blindly retry a command whose original business condition may
no longer hold.

## Tenant isolation

When tenancy is required:

- resolve tenants from a trusted authentication, authorization, or routing
  boundary;
- scope `ITenantAccessor` to the request or worker operation;
- keep append IDs unique per tenant and command;
- use explicit tenant APIs only for authenticated administrative or background
  workflows.

EventLoom enforces matching scoped and explicit tenant values, but it cannot
decide whether your application correctly authenticated the tenant.

## Backup and operations

Back up the EventLoom PostgreSQL schema or MongoDB database together with the
application data that depends on it. Event rows are immutable facts; do not
update or delete them with ad hoc SQL or direct collection edits.

Monitor:

- store availability and latency;
- append failure rates and retry rates;
- projection lag and unresolved failures;
- outbox backlog and failed attempts;
- storage growth;
- MongoDB replica-set health or PostgreSQL lock/connection pressure,
  depending on the provider.

`AddEventLoomHealthChecks()` registers connectivity, schema compatibility,
projection, and outbox readiness checks. `MapEventLoomAdminDiagnostics(...)`
adds protected aggregate-only diagnostics.

## Recover workers deliberately

Treat an unhealthy projection check as an operational incident: inspect its
explicit tenant-scoped failure records, correct the handler or dependency, then
resume the paused projection. Skipping an event intentionally creates a
read-model gap and must be an authorized, audited decision. Replay resets only
the checkpoint, so production rebuilds should normally use a new projection
version and a new or shadow table or collection.

For a degraded outbox check, inspect the tenant-scoped backlog and delivery
attempt history. Fix publisher connectivity or destination behavior before
allowing retries to drain the backlog. Downstream consumers must deduplicate
using the stable outbox message ID because delivery is at least once.

Projection and outbox workers use fenced leases. Releasing a lease expires it
instead of deleting it, so fencing tokens continue increasing across releases.
Expose repair actions only to authorized operators.
