# EventLoom

EventLoom is an opinionated event-sourcing library for .NET 10 and EF Core.

It provides a dedicated EF Core event-store context for PostgreSQL and SQLite,
typed aggregates, versioned events, transactional append/read APIs, configured
aggregate repositories, optional required tenancy, PostgreSQL retry handling,
checkpointed asynchronous and inline projections, worker leases, and optional
snapshots. Every committed event also creates a transactional outbox message
for at-least-once external delivery.

## Status

The project is pre-release and packages are not published yet. The current
branch includes the domain kernel, event registration and JSON evolution,
relational storage, transactional append/read APIs, aggregate repositories,
tenancy, PostgreSQL concurrency hardening, and a production-shaped ordering API
sample, including snapshots, projections, outbox delivery, health checks, and
OpenTelemetry instrumentation.

The repository license will be selected before the first public release.

## Development

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
pnpm --dir docs build
```

Start with the documentation site in [docs](docs/README.md), especially the
[installation](docs/src/content/docs/getting-started/installation.md),
[first aggregate](docs/src/content/docs/getting-started/first-aggregate.md),
[storage configuration](docs/src/content/docs/guides/configure-ef-core.md), and
[production deployment](docs/src/content/docs/guides/production-deployment.md)
guides. Architecture decisions are in
[docs/architecture/decisions](docs/architecture/decisions).

Try the sample:

```bash
dotnet run --project samples/EventLoom.Ordering.AppHost
```

Aspire starts PostgreSQL and the Ordering API, then prints the dashboard URL.
Use its Resources page to open the API, inspect logs, and view ASP.NET Core and
EventLoom OpenTelemetry signals. The API provides its generated OpenAPI
document at `/openapi/v1.json` and Scalar reference UI at `/scalar/v1`.