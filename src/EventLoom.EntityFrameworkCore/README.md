# EventLoom.EntityFrameworkCore

`EventLoom.EntityFrameworkCore` persists EventLoom events through a dedicated
EF Core event-store context. It includes transactional appends, aggregate
repositories, versioned snapshots, checkpointed projections, and the
transactional outbox model.

Add exactly one storage provider:

- `EventLoom.EntityFrameworkCore.PostgreSql` for distributed production
  deployments;
- `EventLoom.EntityFrameworkCore.Sqlite` for local and controlled single-node
  deployments.

This pre-release package targets .NET 10. See the
[EF Core configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md)
before configuring persistence.
