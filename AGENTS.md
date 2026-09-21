# AGENTS.md

This file applies to the entire repository. A more deeply nested `AGENTS.md`
takes precedence for files in its directory; in particular, also follow
`docs/AGENTS.md` when changing the documentation site.

## Repository purpose

EventLoom is an opinionated event-sourcing framework for .NET 10 and EF Core
10. It is a monorepo containing the runtime libraries, storage providers,
hosting integrations, analyzers, testing utilities, tests, documentation, a
sample application, and a `dotnet new` template.

The project deliberately favors explicit contracts and operationally safe
defaults:

- persisted events are immutable, named, and versioned;
- aggregates change state by raising and applying domain events;
- stream versions provide optimistic concurrency;
- tenant boundaries and ordering guarantees must remain explicit;
- projections and outbox delivery are at least once, so consumers must be
  idempotent;
- PostgreSQL is the production/distributed provider;
- SQLite is for local, embedded, test, and controlled single-process use;
- snapshots optimize replay but never replace event history;
- default telemetry must not expose event payloads or identifiers.

Treat changes to persistence semantics, serialization, event ordering,
tenancy, retries, leases, projections, snapshots, or outbox behavior as
compatibility-sensitive.

## Repository map

### Product libraries (`src/`)

All projects below are packable NuGet packages.

| Project                                    | Responsibility                                                                                                                                                                          |
| ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `EventLoom`                                | Dependency-light domain kernel: aggregates, event contracts, envelopes, identifiers, expected versions, tenancy, serialization, upcasting, snapshots, projections, and telemetry names. |
| `EventLoom.EntityFrameworkCore`            | Provider-neutral EF Core persistence: entities and model, event store, repositories, unit of work, snapshots, projections, outbox, retries, and worker leases.                          |
| `EventLoom.EntityFrameworkCore.PostgreSql` | PostgreSQL registration, schema conventions, and retry behavior required for distributed ordering and workers.                                                                          |
| `EventLoom.EntityFrameworkCore.Sqlite`     | SQLite registration, schema conventions, native SQLite setup, and explicitly single-node behavior.                                                                                      |
| `EventLoom.Hosting`                        | Dependency injection, aggregate/projection registration, hosted projection and outbox workers, health checks, worker identity, options, and OpenTelemetry wiring.                       |
| `EventLoom.AspNetCore`                     | ASP.NET Core application and endpoint integration over `EventLoom.Hosting`.                                                                                                             |
| `EventLoom.Testing`                        | Given/When/Then aggregate scenarios, deterministic IDs/time, and a managed SQLite test host. It intentionally does not provide a managed PostgreSQL host.                               |
| `EventLoom.Analyzers`                      | Roslyn diagnostics for persisted event contracts. This project targets `netstandard2.0`; do not accidentally move it to the runtime target framework.                                   |

Keep the intended dependency direction:

```text
EventLoom
  -> EventLoom.EntityFrameworkCore
       -> EventLoom.Hosting
            -> EventLoom.AspNetCore
       -> PostgreSql / Sqlite providers

EventLoom.Testing -> EventLoom + Hosting + Sqlite
EventLoom.Analyzers -> Roslyn only
```

Provider-specific behavior belongs in its provider project. ASP.NET-specific
behavior belongs in `EventLoom.AspNetCore`; host-neutral registration and
workers belong in `EventLoom.Hosting`. Do not make the domain kernel depend on
EF Core, hosting, or a storage provider.

Each package has a package-facing `README.md` beside its project file. Keep it
accurate for the APIs and support boundary of that package.

### Tests (`tests/`)

Tests use TUnit and are executable test projects. The CI-supported invocation
is `dotnet run --project ...`, not an assumed test-framework command.

| Project                                                     | Coverage                                                                                                         |
| ----------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| `EventLoom.UnitTests`                                       | Domain kernel, registry, serialization, and event/snapshot upcasting.                                            |
| `EventLoom.Analyzers.Tests`                                 | Analyzer diagnostics and accepted event-contract shapes.                                                         |
| `EventLoom.EntityFrameworkCore.UnitTests`                   | EF model, hosting registration, workers, health checks, tenancy, options, and provider-independent behavior.     |
| `EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests`     | Real SQLite append/read, repositories, transactions, projections, outbox, and leases.                            |
| `EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests` | Real PostgreSQL behavior, concurrency, ordering, and leases through Testcontainers. Docker must be available.    |
| `EventLoom.PackageConsumerTests`                            | Consumer compiled and run against packed NuGet artifacts only. It must remain independent of project references. |

### Samples (`samples/`)

- `EventLoom.Ordering.Api` is the end-to-end example. It demonstrates domain
  events and aggregates, tenant access, PostgreSQL registration, projections,
  outbox publishing, OpenAPI, and endpoints.
- `EventLoom.Ordering.AppHost` starts PostgreSQL and the API through .NET
  Aspire.
- `EventLoom.ServiceDefaults` contains the sample's service discovery,
  resilience, health, and OpenTelemetry defaults.
- `EventLoom.Samples.slnx` is the sample-only solution.

The sample is executable documentation. Update it when a recommended API or
composition pattern changes.

### Template (`templates/eventloom-api/`)

This is the source for the `eventloom-api` `dotnet new` template. It consumes
released-style package references rather than project references. Keep its
package versions, registration code, and configuration aligned with the
supported public API. CI installs, generates, restores, and builds it from the
locally packed packages.

### Documentation (`docs/`)

The documentation site uses Astro 7 and Starlight, with pnpm. Content lives in
`docs/src/content/docs/`; navigation is explicit in `docs/astro.config.mjs`.
Follow `docs/AGENTS.md` as well as this file for any work under `docs/`.

The content types are intentional:

- **Start here** provides ordered onboarding.
- **Core concepts** explain stable ideas and trade-offs.
- **Guides** provide complete task-oriented procedures, constraints, and
  verification/recovery steps.
- **Reference** records exact configuration, compatibility, guarantees, and
  terminology.

## Toolchain and shared configuration

- Use the .NET SDK selected by `global.json` (currently .NET SDK 10.0.301 with
  latest-patch roll-forward).
- Runtime, test, sample, and template projects target `net10.0`; the analyzer
  targets `netstandard2.0`.
- `Directory.Build.props` enables nullable reference types, latest C#,
  deterministic builds, code-style enforcement, XML documentation, and
  warnings as errors.
- Package versions are centralized in `Directory.Packages.props`. Add or
  update a version there rather than specifying versions in individual
  projects, except where a consumer/template intentionally represents an
  external package user.
- .NET restores use lock files. If dependencies change, regenerate and commit
  every affected `packages.lock.json`; normal validation must then succeed
  with `--locked-mode`.
- Documentation dependencies use `docs/pnpm-lock.yaml`. Use pnpm and preserve
  the frozen-lockfile build.
- Follow `.editorconfig`: UTF-8, LF endings, final newline, two-space default
  indentation, and four-space C# indentation.
- Never edit or commit generated/build output such as `bin/`, `obj/`,
  `artifacts/`, `docs/node_modules/`, `docs/dist/`, or `.vercel/`.

## Implementation rules

1. Read the relevant package README, tests, docs pages, and adjacent
   implementation before changing behavior.
2. Preserve public contracts and operational guarantees (when we have reached a stable release) unless the requested
   change explicitly changes them.
3. Keep event type names and versions stable. Model event evolution through
   explicit versioning and upcasters; never rewrite persisted history.
4. Preserve tenant scoping on every persisted query and mutation. Do not
   weaken optimistic concurrency or global ordering semantics.
5. Keep aggregate operations short. Aggregate state transitions belong in
   `Apply` handlers and must be reproducible during replay.
6. Assume projections and outbox publishers can receive duplicate work.
   Changes must remain safe for at-least-once processing.
7. Keep provider-neutral contracts in `EventLoom.EntityFrameworkCore` and
   isolate provider SQL, capabilities, and transient-failure handling.
8. Do not imply distributed guarantees for SQLite. Validate distributed
   behavior against PostgreSQL.
9. Use cancellation tokens and asynchronous EF/hosting APIs consistently.
   Do not hide storage, serialization, or worker failures.
10. Follow existing TUnit style: `[Test]`, focused behavior-oriented method
    names, async assertions, and deterministic fixtures. Prefer injected
    clocks/ID generators over timing sleeps or nondeterministic assertions.
11. Avoid unrelated refactoring. Update all directly affected surfaces in the
    same change.

## Tests and documentation are required deliverables

The test suite, XML docs and documentation site are part of the implementation, not optional
cleanup. An agent must not consider a change complete until all are complete and accurate.

For every behavior change:

1. Add or update the smallest relevant unit test.
2. Add or update provider integration coverage when persistence, SQL,
   transactions, retries, leases, ordering, or provider capabilities change.
3. Add analyzer tests for each new diagnostic and for accepted/rejected edge
   cases.
4. Update package-consumer or template coverage when package dependencies,
   package contents, registration APIs, or getting-started code changes.
5. Update existing tests whose assertions describe the old behavior; never
   leave ignored, commented-out, or knowingly incomplete coverage.
6. Update all affected documentation:
   - root `README.md` for positioning, package selection, quick start, or
     repository-wide workflows;
   - the changed package's `src/<package>/README.md` for package APIs;
   - the appropriate concept, guide, or reference page in
     `docs/src/content/docs/`;
   - `docs/astro.config.mjs` when adding, moving, or removing a page;
   - sample and template code when recommended usage changes;
   - `CONTRIBUTING.md` or this file when contributor workflows change.
7. Ensure code snippets use the current API, configuration names, defaults,
   and support boundaries. Prefer complete, runnable examples over pseudocode.

Use this impact guide:

| Change                                                                       | Minimum accompanying work                                                                              |
| ---------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| Domain contract, serialization, or upcasting                                 | Core unit tests; core package README and relevant concept/reference docs.                              |
| EF model, repository, snapshot, projection, outbox, or unit-of-work behavior | EF unit tests plus affected provider integration tests; guarantees/configuration docs.                 |
| Provider behavior                                                            | Provider tests and provider README; explicitly document differences between PostgreSQL and SQLite.     |
| Hosting, worker, health, or telemetry behavior                               | Hosting unit tests; configuration/observability/operations docs; sample updates when user-facing.      |
| ASP.NET Core endpoint or composition API                                     | Hosting/API tests as applicable; ASP.NET package README; quick-start/sample/template updates.          |
| Analyzer diagnostic                                                          | Positive and negative analyzer tests; analyzer README and event-contract docs.                         |
| Testing utility                                                              | Tests for the utility; `EventLoom.Testing` README and testing guide.                                   |
| Public API or package graph                                                  | Relevant tests, package README, package docs, local pack/consumer validation, and template validation. |

## Public API and packaging

- Treat all types and members visible to package consumers as compatibility
  commitments (when we have reached a stable version).
- Follow the public API baseline policy in `CONTRIBUTING.md`. When baseline
  files are present or introduced, keep nullable-aware
  `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` entries synchronized
  with intentional API changes. Do not remove or alter shipped signatures
  without an explicitly documented compatibility decision.
- Every project under `src/` is expected to produce exactly one supported
  package. Keep package README and icon inclusion intact.
- `EventLoom.PackageConsumerTests` must restore from `artifacts/packages`
  using its `NuGet.Config`; do not add project references as a shortcut.
- Validate the generated template against locally packed packages after
  changes to public composition APIs, dependency metadata, or template files.

## Validation

Run the smallest relevant checks while iterating, then run the full applicable
set before completion. Commands below run from the repository root.

### Restore and build

```bash
dotnet restore EventLoom.slnx --locked-mode
dotnet build EventLoom.slnx --configuration Release --no-restore
```

The solution build is required for C# changes because warnings are errors and
it catches cross-project API and package issues.

### Test suites

```bash
dotnet run --project tests/EventLoom.UnitTests --configuration Release --no-build
dotnet run --project tests/EventLoom.Analyzers.Tests --configuration Release --no-build
dotnet run --project tests/EventLoom.EntityFrameworkCore.UnitTests --configuration Release --no-build
dotnet run --project tests/EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests --configuration Release --no-build
```

For PostgreSQL behavior, ensure Docker is running and execute:

```bash
dotnet run --project tests/EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests --configuration Release --no-build
```

Do not substitute SQLite tests for PostgreSQL concurrency, lease, or
distributed-ordering coverage.

### Documentation

```bash
pnpm --dir docs install --frozen-lockfile
pnpm --dir docs build
```

Run the documentation build for any content, navigation, configuration, or
code-example change. A new page is incomplete until it is reachable through
the intended navigation.

### Packages, consumer, and template

For package-facing changes, build Release and run:

```bash
./scripts/pack.sh 0.1.0-alpha.0
./scripts/verify-packages.sh 0.1.0-alpha.0
./scripts/validate-packages.sh 0.1.0-alpha.0
```

The scripts reproduce the package job in `.github/workflows/ci.yml` and use an
isolated NuGet cache for consumer and template validation. They restore and run
`EventLoom.PackageConsumerTests` against that local feed, then install and
build a generated `eventloom-api` template.

Prefer the workflow as the source of truth instead of copying its shell loop
into new scripts or documentation. If validation commands change, update CI,
`CONTRIBUTING.md`, root `README.md`, and this file together.

## Completion checklist

Before reporting completion:

- implementation is complete across every affected project;
- focused tests cover success, failure, and compatibility-sensitive cases;
- the relevant test suites pass;
- documentation, package READMEs, samples, and template agree with the code;
- public API baselines and dependency lock files are current when applicable;
- Release build passes without warnings;
- docs build passes when documentation or examples changed;
- PostgreSQL tests ran for distributed/provider behavior, or any inability to
  run Docker is reported explicitly;
- package-consumer/template validation ran for big package-facing changes;
- generated files and unrelated local changes are not included.
