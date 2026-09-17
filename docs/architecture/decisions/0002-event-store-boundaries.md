# ADR 0002: Dedicated Event Store Context

## Status

Accepted

## Decision

EventLoom owns a dedicated `EventStoreDbContext`. PostgreSQL uses a configurable schema and SQLite uses a configurable table prefix. The event store owns its migrations and append operations commit independently by default.

## Consequences

The default persistence boundary is explicit and isolated. Coordinating application data with external effects uses an outbox; shared database transactions are an advanced integration point.