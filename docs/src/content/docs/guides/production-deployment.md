---
title: Production deployment
description: Operate the current EventLoom event store safely in a PostgreSQL application.
---

This guide describes the production boundary supported by the current
pre-release. It does not imply that public package-release tooling is
available.

## Use PostgreSQL and one provider configuration

Use `UsePostgreSql` for a distributed deployment. Configure a stable schema,
table prefix, tenancy mode, and worker identity before production data exists:

```csharp
builder.Services.AddScoped<ITenantAccessor, AuthenticatedTenantAccessor>();
builder.Services.AddEventLoom(eventLoom => eventLoom
    .ConfigureTenancy(TenancyMode.Required)
    .ConfigureEventStore(options =>
    {
        options.Schema = "eventloom";
        options.TablePrefix = "eventloom_";
    })
    .ConfigureWorkers(options =>
    {
        options.InstanceId = builder.Configuration["HOSTNAME"]
            ?? Environment.MachineName;
    })
    .RegisterEvent<OrderPlaced>()
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("EventStore")!));
```

The instance ID must uniquely identify a concurrently running process. Do not
change event names, aggregate type names, stream-ID formats, schema names, or
table prefixes after writing production data without an explicit data migration.

## Deploy schema deliberately

EventLoom currently ships the model but not EventLoom-owned migrations. Build
and review migrations for the dedicated `EventStoreDbContext` in your host
application, then apply them as a deployment step before rolling out
application instances.

Do not use `EnsureCreatedAsync` in a production database that is or will be
managed with migrations. Keep the event-store migration history separate from
the application's normal EF Core context.

## Retry and conflict behavior

The PostgreSQL provider retries transient failures, serialization failures, and
deadlocks according to `MaxRetryAttempts` (default `5`). A retry runs the full
append transaction again. Provide a stable `AppendId` for a command that can
be retried after an ambiguous client-visible failure.

Expected-version and unique-constraint conflicts are business-visible. Reload
the aggregate, reassess the command, and decide whether a new command is
appropriate. Never blindly retry a command whose original business condition
may no longer hold.

## Tenant isolation

When tenancy is required:

- Resolve tenants from a trusted authentication, authorization, or routing
  boundary.
- Scope `ITenantAccessor` to the request or worker operation.
- Include the tenant in application logs and correlation metadata.
- Keep append IDs unique per tenant and command.
- Use explicit tenant APIs only for authenticated administrative or background
  workflows.

EventLoom enforces matching scoped and explicit tenant values, but it cannot
decide whether your application correctly authenticated the tenant.

## Backup and operations

Back up the EventLoom PostgreSQL schema together with the application data that
depends on it. Event rows are immutable facts; do not update or delete them
with ad hoc SQL. Monitor database availability, append latency, lock waits,
retry rates, failed command responses, and storage growth.

Use `EventStoreSchema.ValidateAsync(context)` as a read-only deployment gate
for the EventLoom tables and mapped columns. `AddEventLoomHealthChecks()`
registers connectivity, schema compatibility, projection, and outbox readiness
checks; see [Observability](./observability/). EventLoom runs registered
projections and persists their checkpoints and failures; inspect and repair
them through `ProjectionAdministration`.
Register an `IOutboxPublisher` for external integration and use its stable
message ID as the transport idempotency key. See
[Outbox and application integration](./outbox/) for the delivery and
shared-transaction boundaries.

## Recover workers deliberately

Treat an unhealthy projection check as an operational incident: inspect its
explicit tenant-scoped failure records, correct the handler or dependency,
then resume the paused projection. Skipping an event intentionally creates a
read-model gap and must be an authorized, audited decision. Replay resets only
the checkpoint, so production rebuilds should normally use a new projection
version and a new or shadow read-model table.

For a degraded outbox check, inspect the tenant-scoped backlog and delivery
attempt history. Fix publisher connectivity or destination behavior before
allowing retries to drain the backlog. Downstream consumers must deduplicate
using the stable outbox message ID because delivery is at least once.

Expose projection resume, skip, replay, outbox inspection, and health details
only to authorized operational administrators. Never expose event payloads,
metadata headers, or tenant-scoped operational records through an unauthenticated
endpoint.
