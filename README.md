# EventLoom

EventLoom is an opinionated event sourcing framework for .NET 10 and EF Core.

It will provide a dedicated EF Core event-store context for PostgreSQL and SQLite, typed aggregates, versioned events, snapshots, asynchronous projections, optional tenancy, and tooling for reliable distributed deployments.

## Status

The project is in early development. Phase 0 establishes the repository, delivery, test, and documentation foundations.

The repository license will be selected before the first public release.

## Development

```bash
dotnet build EventLoom.slnx
dotnet run --project tests/EventLoom.UnitTests
pnpm --dir docs build
```

See the documentation site in [docs](docs/README.md) and the architecture decisions in [docs/architecture/decisions](docs/architecture/decisions).