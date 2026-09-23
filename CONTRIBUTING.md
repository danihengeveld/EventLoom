# Contributing to EventLoom

Use the .NET SDK selected by `global.json` and pnpm for the documentation site.
Before opening a pull request, run the smallest relevant validation locally.
The full pull-request checks are defined in `.github/workflows/ci.yml` and
`.github/workflows/security.yml`.

```bash
dotnet restore EventLoom.slnx --locked-mode
dotnet build EventLoom.slnx --configuration Release --no-restore
dotnet run --project tests/EventLoom.UnitTests --configuration Release --no-build
pnpm --dir docs build
```

Changes to persistence guarantees, public APIs, or operational behavior require an architecture decision record or an update to an existing decision.

## Asynchronous continuations

Use `ConfigureAwait(false)` in context-independent library I/O and worker waits
when their continuations do not need the caller's synchronization context.
Do not apply it mechanically to methods that may invoke application-supplied
projection handlers, publishers, retry operations, snapshot policies, or
upcasters after an await: their invocation context must be reviewed first.
Application examples and tests normally use plain `await`.

## Public API and package validation

The eight supported packages have public API regression tests in the core and
EF Core test projects. Update those snapshots only for deliberate API changes.
After the first stable release, do not remove or alter a shipped signature
without an explicitly documented compatibility decision.

CI packs exactly the supported projects and restores
`tests/EventLoom.PackageConsumerTests` from that local package feed. Keep the
consumer scenario independent of project references so it continues to verify
the actual NuGet dependency graph. Reproduce the package checks with:

```bash
./scripts/pack.sh 0.1.0-alpha.0
./scripts/verify-packages.sh 0.1.0-alpha.0
./scripts/validate-packages.sh 0.1.0-alpha.0
```

The validation script uses an isolated NuGet package cache so a locally cached
package with the same version cannot hide a packaging error.

See `RELEASING.md` for the complete release and repository setup process.

## Benchmarks

The non-packable BenchmarkDotNet project in `benchmarks/EventLoom.Benchmarks`
measures event serialization, upcasting, aggregate replay, and PostgreSQL
append/read/load and operational health queries. Build and run it in Release
mode:

```bash
dotnet run --project benchmarks/EventLoom.Benchmarks --configuration Release -- --filter '*SerializationBenchmarks*' '*UpcastingBenchmarks*' '*ReplayBenchmarks*'
```

Use `--list flat` to see benchmark names and `--filter '*PostgreSql*'` for the
PostgreSQL cases. The PostgreSQL benchmarks require a running Docker daemon;
Testcontainers starts an isolated `postgres:18-alpine` container during
benchmark setup and disposes it afterward. No database connection string or
manual schema setup is required. Container startup and seeding are outside
the measured operations.

Record the .NET SDK, OS, CPU, Docker configuration, and workload
parameters alongside timings and allocations. Run comparisons on the same
machine and Docker setup; containerized PostgreSQL measurements are not
representative of every production database environment. The append case seeds
a fresh one-event stream outside each measured invocation, keeping the stream
length fixed; the database still grows during a run. Snapshot-load cases have
a persisted snapshot and a tail of up to ten events. SQLite is intentionally
not benchmarked.
