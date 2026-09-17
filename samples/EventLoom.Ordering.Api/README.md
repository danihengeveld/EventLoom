# EventLoom Ordering API

This is a small production-shaped ASP.NET Core application showing:

- immutable, versioned domain events;
- an aggregate rebuilt from its event stream;
- transactional append and reload through `AggregateRepository`;
- PostgreSQL as the default provider;
- SQLite as an explicit local-development alternative.

Run locally without PostgreSQL:

```bash
EVENTLOOM_DATABASE_PROVIDER=sqlite \
  dotnet run --project samples/EventLoom.Ordering.Api
```

Run with PostgreSQL:

```bash
docker compose -f samples/EventLoom.Ordering.Api/compose.yaml up -d
ConnectionStrings__EventStore='Host=localhost;Database=eventloom;Username=eventloom;Password=eventloom' \
  dotnet run --project samples/EventLoom.Ordering.Api
```

The sample creates the EventLoom schema on startup for convenience. A deployed
application should run reviewed migrations as part of its release process before
starting application instances.

Create and read an order:

```bash
curl -X POST http://localhost:5000/orders \
  -H 'content-type: application/json' \
  -d '{"sku":"coffee","quantity":2}'

curl http://localhost:5000/orders/{id}
```
