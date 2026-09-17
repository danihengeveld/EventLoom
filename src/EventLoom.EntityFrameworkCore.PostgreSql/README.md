# EventLoom.EntityFrameworkCore.PostgreSql

`EventLoom.EntityFrameworkCore.PostgreSql` configures EventLoom's EF Core
event store for PostgreSQL. It provides transient-failure retry classification,
transactionally ordered tenant offsets, and fenced worker leases for
multi-instance deployments.

Register it through `UsePostgreSql` on `EventLoomBuilder`. It is the supported
provider for distributed production deployments.

This pre-release package targets .NET 10. Follow the
[production deployment guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/production-deployment.md)
for migrations, tenancy, and operational requirements.
