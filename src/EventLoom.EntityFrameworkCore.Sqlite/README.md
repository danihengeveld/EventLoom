# EventLoom.EntityFrameworkCore.Sqlite

`EventLoom.EntityFrameworkCore.Sqlite` is the EventLoom provider for SQLite.
It initializes its bundled native SQLite dependency and configures EventLoom
storage without schemas.

Use this as the application entry point for local, embedded, test, or one
controlled-process scenarios:

> **Planned package command:** EventLoom packages are not published to NuGet
> yet. Use the [installation guide](../../docs/src/content/docs/getting-started/installation.md)
> to reference projects from a checkout.

```bash
dotnet add package EventLoom.EntityFrameworkCore.Sqlite --prerelease
```

Configure it through the hosting API:

```csharp
using EventLoom;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;

builder.Services
    .AddEventLoom()
    .UseSqlite("Data Source=eventloom.db")
    .AddEvent<OrderPlaced>();
```

For an intentionally explicit empty-database initialization, resolve the
dedicated `EventStoreDbContext` during startup and call
`EventStoreSchema.EnsureCreatedAsync(context)`. Provider registration
does not create or migrate storage automatically. This operation creates the
configured tables and table prefix, but does not evolve an existing schema.

The package restores the core, EF Core, and hosting packages transitively.
ASP.NET Core applications should reference `EventLoom.AspNetCore` alongside
this provider for application composition and endpoint integration. SQLite
does not support distributed workers or multiple application instances against
one event store. Use
`EventLoom.EntityFrameworkCore.PostgreSql` for production distributed
deployments.

Packages are currently pre-release and not published to NuGet. See the
[EF Core configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md)
for provider setup and migration boundaries.
