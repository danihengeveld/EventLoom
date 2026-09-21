# EventLoom.EntityFrameworkCore.PostgreSql

`EventLoom.EntityFrameworkCore.PostgreSql` is the EventLoom provider for
PostgreSQL. It provides transactional tenant offsets, transient-failure retry
classification, and fenced worker leases for multi-instance deployments.

Use this as the normal application entry point for EventLoom persistence:

```bash
dotnet add package EventLoom.EntityFrameworkCore.PostgreSql --prerelease
```

Configure it through the hosting API:

```csharp
using EventLoom;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;

builder.Services
    .AddEventLoom()
    .UsePostgreSql(builder.Configuration.GetConnectionString("EventStore")!)
    .AddEvent<OrderPlaced>();
```

For an intentionally explicit empty-database initialization, resolve the
dedicated `EventStoreDbContext` during startup and call
`EventStoreSchema.EnsureCreatedAsync(context)`. Provider registration
does not create or migrate storage automatically. This operation creates the
configured schema and table prefix, but does not evolve an existing schema;
production migrations remain owned and reviewed by the host application's
deployment process.

The package restores the core, EF Core, and hosting packages transitively.
ASP.NET Core applications should reference `EventLoom.AspNetCore` alongside
this provider for application composition and endpoint integration. Use
PostgreSQL for any production application that runs multiple instances, uses
distributed projection or outbox workers, or needs the provider's distributed
correctness guarantees.

EventLoom is currently pre-release. Follow the
[EF Core configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md)
and [production deployment guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/production-deployment.md)
for schema, tenancy, and recovery requirements.
