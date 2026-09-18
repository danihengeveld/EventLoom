# EventLoom Architecture Discussion History

This document preserves the substantive planning discussion that preceded implementation. It is a project artifact, not a replacement for formal architecture decision records.

## Goal

Create EventLoom: a professional, opinionated, open-source .NET 10 NuGet library for event sourcing on EF Core. It should prioritize developer experience, reliability, performance, configurable extension points, projections, snapshots, a comprehensive TUnit suite, and clear production behavior in distributed deployments.

## Initial Architecture Direction

- Target .NET 10 and EF Core 10.
- Use System.Text.Json, including support for source-generated serializer metadata.
- Publish a package family rather than one monolithic package.
- Support PostgreSQL and SQLite. PostgreSQL is the distributed production provider; SQLite is for local development, tests, embedded, and controlled single-node use.
- EF Core InMemory is useful for limited fast behavior tests but cannot prove transaction, concurrency, relational-query, or performance correctness.
- Use real relational integration tests in addition to TUnit unit and InMemory tests.

## Package Family

```text
EventLoom
EventLoom.EntityFrameworkCore
EventLoom.EntityFrameworkCore.PostgreSql
EventLoom.EntityFrameworkCore.Sqlite
EventLoom.Hosting
EventLoom.Testing
```

## Persistence Boundary

### Options Discussed

Sharing the application DbContext allows application state, events, inline projections, and an outbox to commit together. It also introduces model and migration coupling, ambiguous SaveChanges ownership, interaction with application interceptors and query filters, shared performance tuning, and less predictable background worker setup.

A dedicated EventStoreDbContext in the same physical database isolates the model, migrations, append semantics, pooling, tracking, and operational behavior. It requires an explicit coordination strategy for application data and event writes: an outbox by default and an advanced shared-transaction API when both contexts use the same relational database.

### Decision

Use a dedicated `EventStoreDbContext`.

- PostgreSQL uses a configurable schema, expected to default to `eventloom`.
- SQLite uses configurable table prefixes because schemas are unavailable.
- EventLoom owns its migrations.
- Appends commit independently by default.
- External integration uses a transactional outbox.
- Cross-context transaction enlistment remains an advanced opt-in API.

## Domain Events and Aggregates

### Events

- Events are immutable application-owned records.
- Events implement `IDomainEvent<TAggregate>` to declare their owning aggregate.
- Aggregate or stream identity does not appear in the event payload.
- Persisted envelope metadata owns stream identity, tenant, event ID, event type, schema version, stream version, position, timestamps, correlation, causation, actor, and headers.
- Stable event identity and schema version live on the event type:

```csharp
[EventType("shopping-cart.product-added", Version = 1)]
public sealed record ProductAdded(ProductId ProductId, int Quantity) : IDomainEvent<Cart>;
```

- Registration is explicit by default with `RegisterEvent<T>()`; assembly scanning is optional.
- Stored type names must never be inferred from CLR names.

### Aggregates

- Use opinionated `Aggregate<TId>` base classes.
- Aggregates raise events and mutate only through private typed `Apply(TEvent)` methods.
- Initial event dispatch uses cached reflection with compiled delegates.
- A source-generated dispatcher remains a future AOT/performance improvement, behind an internal dispatcher abstraction. It is deferred because Roslyn generator maintenance, diagnostics, inheritance, generics, overloads, and IDE compatibility add material complexity.

## Aggregate IDs

- Domain APIs use strongly typed aggregate IDs.
- EventLoom persists a canonical string form plus a stable aggregate type name.
- Built-in and explicit converters support Guid, string, ULID, and application-specific strongly typed IDs.
- EventLoom must not require IDs to implement a framework interface.

Example:

```csharp
public readonly record struct CartId(Guid Value);

events.RegisterAggregate<ShoppingCart, CartId>(
    name: "shopping-cart",
    id => id.Value.ToString("N"),
    value => new CartId(Guid.ParseExact(value, "N")));
```

## Serialization and Evolution

- System.Text.Json is the default serializer.
- Support registering multiple `JsonSerializerContext` instances for source generation.
- Keep a convenient reflection fallback; support strict source-generated mode for trimming/AOT constraints.
- Use versioned JSON upcasters rather than retaining historical CLR event types.
- Default upcaster API should be ergonomic over `JsonElement`; a byte-oriented advanced API can follow for allocation-sensitive scenarios.
- Upcasters must be deterministic and side-effect free. They may use fixed migration constants but not databases, clocks, external services, or unstable runtime configuration.
- Registration validates duplicate event names, duplicate CLR registrations, invalid versions, and incomplete or ambiguous upcaster chains.

## Event Storage and Append Correctness

Storage tables will cover streams, events, tenant offsets, snapshots, projection checkpoints, projection leases, projection failures, and outbox records.

Essential constraints include:

- Unique `(TenantId, StreamId, StreamVersion)`.
- Unique event ID.
- Unique tenant offset within a tenant.
- Indexes for tenant/position and tenant/stream/version access paths.

Append invariant:

> Either the stream version advances and all proposed events are inserted exactly once, or nothing changes.

Write behavior:

1. Load or create the stream head.
2. Check expected version.
3. Advance the stream head using optimistic concurrency.
4. Allocate consecutive stream versions.
5. Allocate committed-order tenant offsets.
6. Insert immutable event rows and optional outbox rows.
7. Commit atomically.
8. Translate provider exceptions into stable EventLoom exceptions.

The unique stream-version index is a final correctness boundary. A first-write race can become a provider-specific unique constraint violation rather than an EF `DbUpdateConcurrencyException`.

## Distributed Systems

Distributed safety is a core invariant, not a deployment switch.

- Multiple application instances must safely append and project concurrently.
- Correctness must not rely on in-process locks or caches.
- PostgreSQL handles distributed worker operation; SQLite validation should reject distributed worker mode by default.
- PostgreSQL retry classification must cover serialization conflicts, deadlocks, and transient connectivity failures.
- Hosted worker topology, node identity, retry policy, lease durations, polling interval, and batch size are configurable.

### Ordering

UUIDv7 event IDs are time-sortable enough for index locality, but are not an authoritative order because events in the same millisecond and clock corrections can reorder them.

Decision: use transactionally serialized tenant offsets for v1. PostgreSQL sequences alone are insufficient because sequence values can be observed out of commit order, which can make consumers skip a late-committing event.

Partitioned offsets are a later throughput feature with explicit ordering tradeoffs.

### Idempotency

- Generate UUIDv7 event IDs when events are raised.
- Support caller-provided `AppendId` for safely retrying whole commands after ambiguous timeout or connection failure.
- Database uniqueness constraints enforce event and append idempotency.

## Multi-Tenancy

- Tenancy is optional at framework configuration time and enabled as either disabled or required, not per operation.
- When enabled, normal request operations resolve tenancy from a scoped `ITenantAccessor`.
- Absence of a required tenant fails before database access.
- Background and administrative APIs require explicit tenant IDs.
- Projections use the tenant captured in each event envelope.
- Every storage key, checkpoint, lease, query, and uniqueness rule includes tenant identity.

Example contract:

```csharp
public interface ITenantAccessor
{
    TenantId? Current { get; }
}
```

No mutable global or AsyncLocal tenant state is part of EventLoom.

## Snapshots

- Snapshots are performance artifacts and never sources of truth.
- Use explicit, versioned snapshot DTOs rather than serializing aggregates.
- Aggregates explicitly convert to and restore from snapshots.
- Load the newest valid snapshot, then replay the remaining stream.
- If a snapshot is corrupt or incompatible, safely fall back to complete replay.
- Default retention is the latest snapshot, with configurable policies such as every N events.

## Projections

- Projection handlers receive typed `EventEnvelope<TEvent>` values to access both event data and operational metadata.
- Asynchronous projections are the default.
- Inline projections require explicit registration and run in the append transaction; they must not make network calls.
- Public delivery guarantee is at-least-once.
- EF read-model updates and checkpoints committed in the same transaction provide effectively-once database effects.
- Never claim general exactly-once processing, especially across external systems.
- External side effects use an outbox and idempotency keys.
- Projection workers use leases with monotonically increasing fencing tokens. Lease timeout alone is unsafe because a paused worker can resume after losing ownership.
- Failures use bounded retries, persisted diagnostics, then pause the affected projection. Resume or skip requires explicit administration.
- Rebuilds use versioned/shadow read models; never drop an active production read model before a rebuild completes.

## Metadata and Operations

- Built-in envelope metadata: event ID, append ID, tenant, stream and aggregate identity, tenant offsets, timestamps via `TimeProvider`, correlation ID, causation ID, actor ID, and application headers.
- Expose dependency-free .NET activities/metrics compatible with OpenTelemetry, structured logging, health checks, projection lag/failure telemetry, and explicit schema compatibility checks.
- Never log payload values by default.
- Apply database migrations explicitly through a CLI/API, never automatically at application startup.

## Testing Strategy

Use TUnit for all test projects. TUnit runs tests in parallel by default, so database fixtures need unique databases/schemas or targeted constraints.

- Unit tests: aggregate behavior, event dispatch, serialization, upcasters, metadata, snapshot policies, retry classification, and options validation.
- EF Core InMemory: fast mapping and repository behavior where relational semantics are irrelevant.
- SQLite in-memory: relational mappings, constraints, rollback, migrations, and atomic checkpoint updates.
- PostgreSQL Testcontainers: concurrent appends, first-write races, ordering, leases, transactions, retries, migrations, and provider exception translation.
- Property/stress tests: no duplicate stream versions, snapshot replay equivalence, deterministic upcasting, projection restart convergence, and interruption around commit boundaries.
- BenchmarkDotNet later measures append, replay, serializer, snapshot, and projection behavior. InMemory must not be used for performance claims.

## Monorepo and Documentation

The repository is a .NET monorepo:

```text
src/           NuGet package projects
tests/         TUnit test projects
benchmarks/    BenchmarkDotNet projects, later
samples/       Tested sample services, later
tools/         CLI and release tooling, later
docs/          Astro/Starlight documentation application
eng/           Shared engineering scripts, later
```

Astro/Starlight is intentionally isolated in `docs/`, including its own `package.json`, pnpm lockfile, and Node dependencies. A root pnpm workspace is unnecessary until multiple JavaScript packages or shared JavaScript tooling exist.

Pin the latest tested Astro/Starlight versions in the lockfile; use automated dependency update pull requests rather than unconstrained version ranges.

## Phase 0 Outcome

Phase 0 was implemented under `~/Repos/Personal/EventLoom`:

- Root Git repository initialized on `main`.
- .NET 10 solution and package-project skeleton created.
- Central NuGet package management, deterministic/strict build policy, and NuGet lockfiles added.
- TUnit test project added with a passing smoke test.
- Astro 7/Starlight documentation site created under `docs/`.
- GitHub Actions validates locked restore, Release build, TUnit, package creation, and docs build.
- .NET CI uses Linux, macOS, and Windows.
- Dependabot, contributor/security/code-of-conduct placeholders, `.editorconfig`, README, and ADRs added.
- Formal license selection remains unresolved and is required before public package publication.

Validation completed successfully:

```bash
dotnet restore EventLoom.slnx --locked-mode
dotnet build EventLoom.slnx --configuration Release --no-restore
dotnet run --project tests/EventLoom.UnitTests --configuration Release --no-build
dotnet pack EventLoom.slnx --configuration Release --no-build --output artifacts/packages
pnpm --dir docs build
```

## Next Step

Phase 1: implement the domain kernel in `EventLoom`: `IDomainEvent<TAggregate>`, `EventTypeAttribute`, the aggregate base class, pending events, reflection-based typed `Apply` dispatch, expected versions, event envelopes, ID converters, and focused TUnit coverage.