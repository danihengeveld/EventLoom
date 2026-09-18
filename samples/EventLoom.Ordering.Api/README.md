# EventLoom Ordering API

This ASP.NET Core sample shows the standard EventLoom application path:

- immutable, explicitly registered, versioned events;
- an `Order` aggregate that changes state only through event application;
- short aggregate-repository load/save operations;
- explicit snapshots captured every two events;
- required request-scoped tenancy;
- correlation metadata and caller-owned idempotency keys;
- stream reconstruction and envelope inspection;
- PostgreSQL composed by .NET Aspire.

## Run with Aspire

```bash
dotnet run --project samples/EventLoom.Ordering.AppHost
```

Running `dotnet run --project samples/EventLoom.Ordering.Api` also selects the
default `Aspire AppHost` launch profile. Use the `API (direct)` profile only
when supplying `ConnectionStrings__EventStore` yourself.

The AppHost starts the PostgreSQL event-store database, injects its connection
string into the API, waits for the database before starting the API, and opens
the Aspire dashboard. Use the dashboard's **Resources** page to open the API
endpoint and view its AppHost-managed URL.

The dashboard receives the API's logs, ASP.NET Core telemetry, and EventLoom
traces and metrics through OpenTelemetry. The API remains PostgreSQL-only so
the sample exercises EventLoom's distributed production provider.

The sample retains successfully published outbox messages and their attempt
history for one day so the outbox inspection endpoint remains useful.

The sample calls `EnsureCreatedAsync` to make an empty local database usable.
Production applications should create and apply reviewed migrations for the
dedicated `EventStoreDbContext` before starting instances.

## API

Every endpoint requires an `X-Tenant-ID` header. The header is deliberately
simple so the sample exposes EventLoom's tenant scoping. In a production
service, resolve it from trusted authentication or routing data instead.

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/orders` | Create an order. |
| `GET` | `/orders/{id}` | Rehydrate and return an order. |
| `POST` | `/orders/{id}/items` | Add an item to a placed order. |
| `POST` | `/orders/{id}/cancel` | Cancel a placed order. |
| `GET` | `/orders/{id}/events` | Inspect persisted envelope metadata. |

Create an order with a stable request ID and idempotency key:

```bash
api_url=http://localhost:5080 # Copy the URL from the Aspire dashboard.
order_id=$(uuidgen | tr '[:upper:]' '[:lower:]')
curl -X POST "${api_url}/orders" \
  -H 'content-type: application/json' \
  -H 'X-Tenant-ID: acme' \
  -H 'Idempotency-Key: place-order-42' \
  -d "{\"orderId\":\"${order_id}\",\"sku\":\"coffee\",\"quantity\":2}"
```

In the Development environment, open the API resource's `/scalar/v1` endpoint
for the Scalar API reference and `/openapi/v1.json` for the generated OpenAPI
document. Scalar preconfigures the required `X-Tenant-ID` header with the sample
tenant `acme`; change it once in Scalar's authentication section to use another
tenant for all requests. See the
[Ordering API guide](../../docs/src/content/docs/guides/ordering-api.md) for the
complete command sequence and design explanation.
