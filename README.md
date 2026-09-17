# EventLoom

EventLoom is an opinionated event sourcing framework for .NET 10 and EF Core.

It provides a dedicated EF Core event-store context for PostgreSQL and SQLite, typed aggregates, versioned events, transactional append/read APIs, and aggregate repositories. Snapshots, asynchronous projections, optional tenancy, and tooling for reliable distributed deployments remain on the roadmap.

## Status

The project is in early development toward its 0.1 release. The current branch includes the domain kernel, event registration and JSON evolution, relational storage, transactional append/read APIs, aggregate repositories, and a production-shaped ordering API sample.

The repository license will be selected before the first public release.

## Development

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
pnpm --dir docs build
```

See the documentation site in [docs](docs/README.md) and the architecture decisions in [docs/architecture/decisions](docs/architecture/decisions).

Try the sample:

```bash
EVENTLOOM_DATABASE_PROVIDER=sqlite \
  dotnet run --project samples/EventLoom.Ordering.Api
```