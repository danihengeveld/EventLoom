---
title: Ordering API sample
description: Run an ASP.NET Core sample using scoped tenants, aggregate commands, snapshots, projections, and an outbox publisher.
---

[`samples/EventLoom.Ordering.Api`](https://github.com/danihengeveld/EventLoom/tree/main/samples/EventLoom.Ordering.Api)
is a compact, production-shaped ASP.NET Core application. It demonstrates:

- explicit registration of three immutable, versioned order events;
- an `Order` aggregate rebuilt from persisted history;
- configured aggregate repository identity and short `LoadAsync` / `SaveAsync`
  operations;
- an explicit order snapshot captured every two events;
- scoped, required tenancy;
- request correlation metadata and caller-provided idempotency keys;
- adding items, cancellation, and inspecting persisted envelope metadata;
- an EF order-summary projection with atomic checkpoint/read-model updates and
  an endpoint for projection health;
- a transport-neutral logging outbox publisher and tenant-scoped delivery
  inspection;
- PostgreSQL composed by .NET Aspire.

## Run with Aspire

```bash
dotnet run --project samples/EventLoom.Ordering.AppHost
```

The AppHost starts PostgreSQL, injects the `EventStore` connection string into
the API, waits for the database before it starts the API, and launches the
Aspire dashboard. Open the dashboard URL printed by the AppHost. Its
**Resources** page provides the AppHost-managed URL for the API.

The sample uses `EnsureCreatedAsync` for a new database. Use reviewed EF Core
migrations before starting production application instances.

The sample intentionally uses only PostgreSQL. That lets it demonstrate
EventLoom's distributed production provider and makes its telemetry available
in the Aspire dashboard through OpenTelemetry.

## Explore the API

In Development, the API resource generates an OpenAPI document with
`Microsoft.AspNetCore.OpenApi` and exposes the Scalar interactive reference at
`/scalar/v1`. The generated document is at `/openapi/v1.json`. Use the API URL
from the Aspire dashboard rather than assuming a fixed local port. These
development-only endpoints are not mapped in production and do not require the
sample's `X-Tenant-ID` header.

The dashboard shows API logs plus ASP.NET Core and EventLoom traces and metrics.
The API also exposes `/health` for its readiness checks and `/alive` for its
process liveness check; both are intentionally available without a tenant
header so Aspire can probe the service.

## Exercise the API

All requests require `X-Tenant-ID`. The header makes tenant isolation visible
in a small sample; a real service should derive the tenant from validated
authentication or routing context.

Create an order. Supplying `orderId` and `Idempotency-Key` lets a client repeat
the same command after an ambiguous response:

```bash
api_url=http://localhost:5080 # Copy the URL from the Aspire dashboard.
order_id=$(uuidgen | tr '[:upper:]' '[:lower:]')

curl -X POST "${api_url}/orders" \
  -H 'content-type: application/json' \
  -H 'X-Tenant-ID: acme' \
  -H 'X-Correlation-ID: checkout-42' \
  -H 'Idempotency-Key: place-order-42' \
  -d "{\"orderId\":\"${order_id}\",\"sku\":\"coffee\",\"quantity\":2}"
```

Add an item:

```bash
curl -X POST "${api_url}/orders/${order_id}/items" \
  -H 'content-type: application/json' \
  -H 'X-Tenant-ID: acme' \
  -d '{"sku":"filter","quantity":1}'
```

Read the rehydrated aggregate:

```bash
curl -H 'X-Tenant-ID: acme' \
  "${api_url}/orders/${order_id}"
```

Inspect persisted envelope metadata, including stream version and tenant
offset:

```bash
curl -H 'X-Tenant-ID: acme' \
  "${api_url}/orders/${order_id}/events"
```

Cancel the order:

```bash
curl -X POST "${api_url}/orders/${order_id}/cancel" \
  -H 'content-type: application/json' \
  -H 'X-Tenant-ID: acme' \
  -d '{"reason":"customer-request"}'
```

The asynchronous order summary is intentionally eventually consistent. Poll it
after sending commands:

```bash
curl -H 'X-Tenant-ID: acme' \
  "${api_url}/orders/${order_id}/summary"
```

Inspect the summary projection's tenant checkpoint and any persisted failures:

```bash
curl -H 'X-Tenant-ID: acme' \
  "${api_url}/projections/order-summary"
```

The sample also exposes explicit tenant-scoped recovery routes:

```text
POST /projections/order-summary/resume
POST /projections/order-summary/replay
POST /projections/order-summary/failures/{eventId}/skip
```

They illustrate the `ProjectionAdministration` API only. A production service
must protect them with an administrator authorization policy. Replay resets
the checkpoint but does not clear the read model; use a new projection version
and shadow table for a production rebuild.

Each event is also written to the outbox. Replace `<event-id>` with the
event's `eventId` from the event-history response to inspect the logging
publisher's durable delivery record and attempts:

```bash
curl -H 'X-Tenant-ID: acme' \
  "${api_url}/outbox/<event-id>"
```

Run the complete command sequence in a single tenant. Repeating it with a
different tenant demonstrates that tenant-scoped streams are isolated.
