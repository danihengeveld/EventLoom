# EventLoom Implementation Roadmap

## Purpose

This roadmap turns the accepted EventLoom architecture into independently releasable implementation phases. Each phase must meet its exit criteria, be documented, and preserve the correctness guarantees established in the architecture discussion history and ADRs.

## Architecture Baseline

- .NET 10 and EF Core 10.
- Dedicated `EventStoreDbContext`, usually in the same physical database as the application.
- PostgreSQL is the distributed production provider. SQLite supports development and single-node scenarios.
- `System.Text.Json` is the default serializer, with source-generated metadata supported from the first functional release.
- Events are immutable records with `[EventType]` metadata. Stream identity and operational data live in envelopes, not event payloads.
- Aggregates derive from opinionated base classes and use cached reflection with compiled delegates to dispatch typed `Apply` methods.
- Strongly typed aggregate IDs use configured canonical string converters.
- Optional tenancy is either disabled or required at configuration time.
- Asynchronous projections are the default. The documented guarantee is at-least-once; EF model and checkpoint changes may be effectively-once inside one transaction.
- Snapshots use explicit versioned DTOs.
- Distributed correctness is always-on for PostgreSQL; runtime worker topology is configurable.

## Phase 0: Repository Foundation

### Deliverables

- .NET 10 solution and EventLoom package-project skeleton.
- Central NuGet package management and committed dependency lockfiles.
- Deterministic, strict nullable builds with warnings as errors.
- TUnit test runner and smoke coverage.
- Astro/Starlight docs application in `docs/`, with isolated pnpm dependencies.
- GitHub Actions validation for locked restore, Release build, tests, NuGet pack, and docs build.
- Validation on Linux, macOS, and Windows.
- Basic contributor, security, conduct, editor, Git ignore, and architecture documentation.

### Exit Criteria

```bash
dotnet restore EventLoom.slnx --locked-mode
dotnet build EventLoom.slnx --configuration Release --no-restore
dotnet run --project tests/EventLoom.UnitTests --configuration Release --no-build
dotnet pack EventLoom.slnx --configuration Release --no-build --output artifacts/packages
pnpm --dir docs build
```

All commands pass from a clean checkout.

### Status

Complete locally. Choose a license, create the GitHub repository, commit, and push before public distribution.

## Phase 1: Domain Kernel

### Ownership

`src/EventLoom` and `tests/EventLoom.UnitTests`.

### Deliverables

- `IDomainEvent` marker interface.
- `EventTypeAttribute` with stable event name and positive schema version.
- `Aggregate<TId>` base class:
  - aggregate identity and persisted version;
  - `Raise` API for new events;
  - uncommitted event tracking and clearing;
  - replay path that never creates uncommitted events;
  - typed private/protected `Apply(TEvent)` dispatch.
- Reflection dispatcher built once per aggregate type and cached as compiled delegates.
- Diagnostics and exceptions for missing, ambiguous, static, invalid-return-type, or inaccessible handlers.
- `ExpectedVersion`: exact version, `NoStream`, `StreamExists`, and `Any` semantics.
- Immutable event-envelope model and immutable metadata model.
- `TenantId` normalization and core tenancy contracts without HTTP dependencies.
- Strongly typed ID conversion contracts and standard converters for string, Guid, and ULID where supported.
- `TimeProvider` and UUIDv7 abstraction points for deterministic testing.

### Tests

- Raise/apply/pending event lifecycle.
- Replay produces equivalent state without pending events.
- Typed handler discovery across inheritance boundaries.
- Missing and duplicate handler failures.
- Expected-version values and equality.
- Envelope/metadata immutability.
- ID converter round trips and invalid canonical values.
- Tenant normalization and invalid input.
- Deterministic time and identifier injection.

### Exit Criteria

An aggregate can be created, evolve from commands, expose pending events, clear them after a simulated commit, and reconstruct exactly from event history with no EF Core dependency.

## Phase 2: Event Registration, Serialization, and Evolution

### Ownership

`src/EventLoom`, `src/EventLoom.Testing`, and corresponding unit tests.

### Deliverables

- Explicit event registry with `RegisterEvent<TEvent>()`.
- Optional assembly scanning extension.
- Validation that every registered event has `[EventType]` metadata.
- Stable type-name lookup from stored name/version to current CLR event type.
- `System.Text.Json` serializer abstraction.
- Registration of multiple `JsonSerializerContext` values.
- Reflection serializer fallback and strict source-generated metadata mode.
- Stable serialized envelope/payload format.
- JSON upcaster contract from one logical event version to the next.
- Ordered upcaster chains with startup validation.
- Explicit unknown-event, unknown-version, corrupt payload, and deserialization error behavior.
- Test builders and assertions for events/envelopes.

### Tests

- Registration errors for duplicates, missing attributes, and invalid versions.
- Reflection and source-generated serialization round trips.
- Cross-context serialization registration.
- Ordered upcast chains and validation of gaps/cycles.
- Upcasted payload reaches the current CLR event representation.
- Error messages include safe identifiers but never payload content.
- Determinism tests for upcasters.

### Exit Criteria

The runtime can serialize a registered event, deserialize it through a validated upcaster chain, and apply the current event representation to an aggregate.

## Phase 3: EF Core Event Storage

### Ownership

`src/EventLoom.EntityFrameworkCore`, provider packages, and provider-specific test projects.

### Deliverables

- Dedicated `EventStoreDbContext` and internal EF entity types.
- Model configuration extension points.
- Framework-owned migration assembly and explicit migration API.
- Common tables:
  - streams;
  - events;
  - per-tenant offsets;
  - snapshots;
  - projection checkpoints;
  - projection leases;
  - projection failures;
  - outbox records.
- PostgreSQL schema configuration and SQLite table-prefix configuration.
- Relational indexes and uniqueness constraints.
- Provider capability abstraction.
- PostgreSQL-specific mappings and exception classification.
- SQLite-specific mappings and capability validation.
- Schema compatibility validation.

### Tests

- EF InMemory mapping smoke tests only where relational behavior is irrelevant.
- SQLite in-memory mapping, migrations, constraints, rollback, and query tests.
- PostgreSQL Testcontainers migration and schema tests.
- Index/constraint inspections where provider APIs permit.
- Ensure table/schema configuration is deterministic.

### Exit Criteria

EventLoom can create and validate its schema through explicit migration operations on PostgreSQL and SQLite. SQLite never advertises unsupported multi-instance worker behavior.

## Phase 4: Append, Read, and Aggregate Repository

### Ownership

`src/EventLoom.EntityFrameworkCore`, provider packages, and SQLite/PostgreSQL tests.

### Deliverables

- Event store append API accepting stream identity, expected version, event batch, metadata, and optional `AppendId`.
- Stream-head concurrency handling.
- Consecutive per-stream versions.
- Per-tenant committed-order offsets.
- UUIDv7 event IDs, with injectable test identifier source.
- Event/append idempotency constraints and result behavior.
- Stream reads by complete history, version ranges, and position batches.
- Aggregate repository load/save APIs.
- Cancellation support and explicit ambiguous-commit guidance.
- Stable EventLoom exceptions translated from provider failures.
- No public `IQueryable` escape hatch.

### Tests

- Append new and existing streams.
- Exact, no-stream, stream-exists, and any-version expectations.
- Batch version and position allocation.
- Load aggregate from history.
- Event ID and append ID idempotency.
- Transaction rollback leaves no partial event batch.
- Cross-tenant isolation.
- Cancellation reaches database APIs where supported.

### Exit Criteria

A sample application can save and reload an aggregate using SQLite and PostgreSQL with stable append semantics.

## Phase 5: Distributed Correctness and Multi-Tenancy

### Ownership

Provider packages, `EventLoom.Hosting`, and PostgreSQL Testcontainers suites.

### Deliverables

- Scoped `ITenantAccessor` integration when tenancy is enabled.
- Explicit tenant APIs for administrative and background processes.
- Tenant-aware keys, indexes, stream reads, append rules, snapshots, offsets, leases, and checkpoints.
- PostgreSQL transient failure, serialization conflict, and deadlock classification.
- Execution-strategy-aware retry policy.
- Transactionally serialized per-tenant offsets.
- PostgreSQL worker lease primitives with monotonic fencing tokens.
- Worker configuration validation, including distributed-worker rejection on SQLite.
- Instance identity, retry, polling, batch, lease and renewal options.

### Tests

- Concurrent appends to one stream never create duplicate versions.
- First-write races resolve to expected conflict/idempotent outcomes.
- Committed-order offsets cannot skip a late commit.
- Conflicting tenant access is impossible.
- Lease loss fences a stale worker.
- Multi-instance stress test with independent contexts/process boundaries.
- Retry only classifies transient failures; logical conflicts are not silently retried.

### Exit Criteria

Multiple PostgreSQL application instances can append and operate workers without corruption, event loss, skipped offsets, or cross-tenant visibility.

## Phase 6: Snapshots

### Ownership

`src/EventLoom`, `src/EventLoom.EntityFrameworkCore`, and tests.

### Deliverables

- Explicit snapshot DTO and aggregate conversion contracts.
- Snapshot envelope with aggregate type, tenant, stream ID, stream version, snapshot type, schema version, payload, and timestamp.
- Snapshot policy registration, including every-N-events default policy.
- Load latest valid snapshot then replay later events.
- Snapshot retention policy with latest-only default.
- Safe invalidation/fallback behavior for corrupt, incompatible, or unavailable snapshots.
- Optional snapshot upcaster/invalidation extension point.

### Tests

- Full replay and snapshot-plus-tail replay produce identical aggregate state.
- Snapshot version boundaries.
- Corrupt snapshot falls back to full replay.
- Retention is tenant and stream isolated.
- Snapshot write failure does not corrupt already committed history.

### Exit Criteria

Snapshots improve replay without becoming a correctness dependency.

## Phase 7: Projection Engine

### Ownership

`src/EventLoom.Hosting`, `src/EventLoom.EntityFrameworkCore`, and PostgreSQL integration tests.

### Deliverables

- Typed handler API over `EventEnvelope<TEvent>`.
- Explicit asynchronous projection registration, enabled by default.
- Explicit inline projection registration for transaction-bound projections only.
- Position-batch reader and projection dispatcher.
- Atomic checkpoint advancement.
- EF projection execution mode, with read model and checkpoint committed in the same context transaction.
- Lease acquisition, renewal, release, and fencing verification on PostgreSQL.
- Bounded retries with backoff.
- Persisted projection failures and paused projection state.
- Administrative resume, skip, inspect, and replay operations.
- Versioned/shadow read-model rebuild workflow.
- Initial strict globally ordered execution; partitioned throughput is a later optional capability.

### Tests

- Handler gets event and envelope metadata.
- Crash/restart causes safe repeat delivery.
- Database read-model and checkpoint effects are atomic.
- Repeated delivery converges for idempotent handlers.
- Poison event retries, persists diagnostics, and pauses only its projection.
- Lease contention and stale-worker fencing.
- Rebuild reaches the same state as initial execution.

### Exit Criteria

Projections are recoverable, observable, at-least-once, and safe across PostgreSQL application instances.

## Phase 8: Outbox and Application Integration

### Ownership

`src/EventLoom.Hosting`, `src/EventLoom.EntityFrameworkCore`, and samples.

### Deliverables

- Transactional EventLoom outbox records written with appends.
- Outbox publisher worker with claim/lease/retry behavior.
- Stable message IDs, attempt history, and idempotency semantics.
- Transport-neutral publisher interface; no message-broker dependency in the core.
- Advanced shared `DbConnection`/`DbTransaction` enlistment path for application and event contexts on the same database.
- Clear documentation that external effects cannot receive a general exactly-once guarantee.

### Tests

- Committed outbox messages survive process interruption.
- Publisher retries without duplicate externally visible effects when the transport honors the idempotency key.
- Shared transaction commits or rolls back both contexts together.
- Incorrect cross-context configuration fails early.

### Exit Criteria

Applications can reliably publish integration messages after committed appends without distributed transactions.

## Phase 9: Observability, Tooling, and Operations

### Ownership

`src/EventLoom.OpenTelemetry`, `src/EventLoom.Hosting`, and `tools/EventLoom.Cli`.

### Deliverables

- OpenTelemetry activities, metrics, and semantic attribute conventions.
- Structured logs with stable event IDs and payload-redaction defaults.
- Health checks for database connectivity, schema compatibility, worker leases, projection failures, and lag.
- Explicit migration, schema validation, projection inspection, replay, resume, and skip CLI commands.
- Operational documentation, recovery playbooks, and deployment guidance.
- Metrics for append latency, conflicts, retries, event/position lag, batch size, projection failures, outbox state, and lease health.

### Tests

- Activities and metrics contain expected non-sensitive attributes.
- Sensitive payload data is absent from normal logs.
- Health checks differentiate healthy, degraded, and unhealthy states.
- Administrative actions require intentional options and validate tenant scope.

### Exit Criteria

Operators can identify and recover from blocked projections, lag, schema mismatch, and delivery failures without directly mutating database records.

## Phase 10: Performance, Compatibility, and Release Hardening

### Ownership

`benchmarks/EventLoom.Benchmarks`, all package projects, docs, and release automation.

### Deliverables

- BenchmarkDotNet suites for append, replay, serialization, snapshot crossover, projection batching, and allocations.
- PostgreSQL soak, contention, and fault-injection jobs.
- Public API compatibility approval/validation.
- Package README, XML documentation, Source Link, symbols, and package validation.
- API and storage compatibility policy.
- Tested samples: shopping cart, multitenant API, and distributed projections.
- Release pipeline producing signed/provenanced packages once signing policy is chosen.
- Versioned Starlight documentation for stable releases and main-branch development.
- Publish policy, changelog, migration guides, and support policy.

### Tests

- Performance regression thresholds for agreed benchmarks.
- Long-running append/project/restart stress tests.
- Package consumer smoke test from packed local NuGet artifacts.
- Documentation code samples compile and run.
- Upgrade tests from supported previous package/schema versions.

### Exit Criteria

At least one realistic service has dogfooded a release candidate. The project has an adopted license, security contact, package ownership, release process, and documented backward-compatibility policy.

## Test Matrix

| Layer | Purpose | Provider |
| --- | --- | --- |
| Unit | Domain invariants and algorithms | None |
| InMemory | Fast EF-adjacent behavior only | EF Core InMemory |
| Relational fake | Constraints, transactions, migrations | SQLite in-memory |
| Production integration | Concurrency, leases, ordering, retries | PostgreSQL Testcontainers |
| Stress/property | Invariants under random and concurrent inputs | PostgreSQL/Testcontainers |
| Benchmarks | Throughput, allocations, regressions | PostgreSQL and focused in-process components |

All tests use TUnit. InMemory tests must never establish a claim about production transaction, relational-query, concurrency, or performance behavior.

## Delivery Discipline

- Keep public APIs minimal and evolve through explicit compatibility review.
- Keep storage migration and event-schema compatibility separate: migrations change tables; upcasters change historical payload interpretation.
- Do not add a feature until its delivery guarantee, failure mode, and operational recovery story are documented.
- Prefer explicit registration over implicit scanning; scanning remains a convenience feature.
- Do not automatically apply migrations in production.
- Do not advertise exactly-once behavior outside one atomic database transaction.
- Do not rely on process-local locks or caches for correctness.
- Document SQLite restrictions prominently and validate unsafe distributed configuration at startup.
- Require a formal license, security reporting path, and package owner before public NuGet publication.