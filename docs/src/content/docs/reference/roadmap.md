---
title: Implementation roadmap
description: EventLoom delivery stages and current boundaries.
---

| Stage | Status | Scope |
| --- | --- | --- |
| Repository foundation | Complete | .NET 10 workspace, build, test, and docs foundations. |
| Domain kernel | Complete | Aggregates, event metadata, IDs, and expected versions. |
| Serialization and evolution | Complete | Explicit registry, JSON serialization, and deterministic upcasters. |
| EF Core storage | Complete | Dedicated context, relational model, SQLite and PostgreSQL providers. |
| Append and aggregate path | Complete | Transactional append/read and configured repositories. |
| Distributed correctness and tenancy | In progress | Tenancy, PostgreSQL retries, tenant offsets, and lease primitives are available; worker execution stress coverage continues. |
| Snapshots | Complete | Versioned DTOs, retention policies, upcasters, invalidation, and replay fallback. |
| Projections | Complete | Checkpointed asynchronous and inline delivery, EF transactions, leases, failures, and administration. |
| Outbox and application integration | Complete | Transactional messages, a leased at-least-once publisher, attempt history, and same-connection transaction enlistment. |

The full engineering plan is maintained in the repository's
[implementation roadmap](https://github.com/danihengeveld/EventLoom/blob/main/docs/architecture/implementation-roadmap.md).
Use the other documentation pages as the source of truth for what can be used
today.