# EventLoom.Hosting

`EventLoom.Hosting` composes EventLoom with `IServiceCollection`. It registers
the event store, configured aggregate repositories, projection and outbox
workers, health checks, and optional OpenTelemetry SDK helpers.

Use it with `EventLoom.EntityFrameworkCore` and one EventLoom provider package.
PostgreSQL supports distributed worker leases; SQLite is only supported for
local or controlled single-node use.

This pre-release package targets .NET 10. See the
[production deployment guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/production-deployment.md)
for deployment and recovery boundaries.
