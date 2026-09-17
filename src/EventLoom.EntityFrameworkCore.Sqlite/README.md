# EventLoom.EntityFrameworkCore.Sqlite

`EventLoom.EntityFrameworkCore.Sqlite` configures EventLoom's EF Core event
store for SQLite through `UseSqlite` on `EventLoomBuilder`.

SQLite is supported for local development, tests, and controlled single-node
deployments only. Do not run multiple application instances or distributed
workers against the same SQLite event store.

This pre-release package targets .NET 10. See the
[storage configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md)
for provider setup.
