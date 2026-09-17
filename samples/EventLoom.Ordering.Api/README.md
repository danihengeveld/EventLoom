# EventLoom Ordering API

This ASP.NET Core sample shows the standard EventLoom application path:

- immutable, explicitly registered, versioned events;
- an `Order` aggregate that changes state only through event application;
- short aggregate-repository load/save operations;
- required request-scoped tenancy;
- correlation metadata and caller-owned idempotency keys;
- stream reconstruction and envelope inspection;
- PostgreSQL by default and SQLite for local use.

## Run locally with SQLite

```bash
EVENTLOOM_DATABASE_PROVIDER=sqlite \
  dotnet run --project samples/EventLoom.Ordering.Api
```

## Run against PostgreSQL

```bash
docker compose -f samples/EventLoom.Ordering.Api/compose.yaml up -d
ConnectionStrings__EventStore='Host=localhost;Database=eventloom;Username=eventloom;Password=eventloom' \
  dotnet run --project samples/EventLoom.Ordering.Api
```

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
order_id=$(uuidgen | tr '[:upper:]' '[:lower:]')
curl -X POST http://localhost:5000/orders \
  -H 'content-type: application/json' \
  -H 'X-Tenant-ID: acme' \
  -H 'Idempotency-Key: place-order-42' \
  -d "{\"orderId\":\"${order_id}\",\"sku\":\"coffee\",\"quantity\":2}"
```

See the [Ordering API guide](../../docs/src/content/docs/guides/ordering-api.md)
for the complete command sequence and design explanation.
