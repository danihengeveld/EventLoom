---
title: Explore the Ordering API sample
description: Run an ASP.NET Core sample using scoped tenants, aggregate commands, snapshots, projections, and an outbox publisher on PostgreSQL or MongoDB.
---

[`samples/EventLoom.Ordering.Api`](https://github.com/danihengeveld/EventLoom/tree/main/samples/EventLoom.Ordering.Api)
is a compact, production-shaped ASP.NET Core application. It demonstrates:

- explicit registration of immutable, versioned order events;
- an `Order` aggregate rebuilt from persisted history;
- configured aggregate repository identity and short `LoadAsync` / `SaveAsync`
  operations;
- an explicit order snapshot captured every two events;
- scoped, required tenancy;
- request correlation metadata and caller-provided idempotency keys;
- a transactional outbox publisher and tenant-scoped delivery inspection;
- **two provider-specific projection implementations** that share the same API
  surface for callers:
  - PostgreSQL uses `IEfProjectionHandler<TEvent>` plus
    `ConfigureProjectionModel(...)`;
  - MongoDB uses `IMongoProjectionHandler<TEvent>` plus a MongoDB collection
    reader;
- provider selection through `EventLoom:Provider = PostgreSql | MongoDb`.

## Run with Aspire

PostgreSQL is the default:

```bash
dotnet run --project samples/EventLoom.Ordering.AppHost
```

Switch the sample to MongoDB:

```bash
dotnet run --project samples/EventLoom.Ordering.AppHost -- --EventLoom:Provider MongoDb
```

The AppHost reads `EventLoom:Provider`, starts the matching backing service, and
passes the provider name into the API:

- `PostgreSql` starts PostgreSQL;
- `MongoDb` starts MongoDB as a **single-node replica set** because EventLoom
  requires MongoDB transactions.

The sample initializes a new store only in Development through
`InitializeEventLoomDevelopmentDatabaseAsync()`.

## Provider-specific composition

The sample keeps provider-specific registration in separate files:

- `Infrastructure/OrderingPostgreSqlExtensions.cs`
- `Infrastructure/OrderingMongoDbExtensions.cs`

That avoids the ambiguous `Transactional<,>()` extension methods that appear if
one file imports both `EventLoom.EntityFrameworkCore` and `EventLoom.MongoDb`.
Both implementations register the same durable projection name,
`ordering.order-summary`.

## Explore the API

In Development, the API resource generates an OpenAPI document with
`Microsoft.AspNetCore.OpenApi` and exposes the Scalar interactive reference at
`/scalar/v1`. The generated document is at `/openapi/v1.json`. Use the API URL
from the Aspire dashboard rather than assuming a fixed local port.

The dashboard shows API logs plus ASP.NET Core and EventLoom traces and metrics.
The API also exposes `/health` for readiness and `/alive` for liveness. All
sample business endpoints require `X-Tenant-ID`.

## Exercise the API

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

Inspect persisted envelope metadata, including stream version and tenant offset:

```bash
curl -H 'X-Tenant-ID: acme' \
  "${api_url}/orders/${order_id}/events"
```

The asynchronous order summary is intentionally eventually consistent. Poll it
after sending commands:

```bash
curl -H 'X-Tenant-ID: acme' \
  "${api_url}/orders/${order_id}/summary"
```

The read path stays the same regardless of provider. Behind that endpoint:

- PostgreSQL reads an EF-mapped `OrderSummary` row;
- MongoDB reads an `ordering_order_summaries` document through
  `MongoSessionAccessor.Database`.

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

Each event is also written to the outbox. This sample retains successful
delivery records for one day. Replace `<event-id>` with the event's `eventId`
from the event-history response to inspect the delivery record and attempts:

```bash
curl -H 'X-Tenant-ID: acme' \
  "${api_url}/outbox/<event-id>"
```

Run the same sequence under both providers to compare behavior. Command and
query endpoints stay stable; only the provider wiring and transactional
projection implementation change.
