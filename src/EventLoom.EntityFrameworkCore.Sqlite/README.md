# EventLoom.EntityFrameworkCore.Sqlite

`EventLoom.EntityFrameworkCore.Sqlite` is the EventLoom provider for SQLite.
It initializes its bundled native SQLite dependency and configures EventLoom
storage without schemas.

Use this as the application entry point for local, embedded, test, or one
controlled-process scenarios:

```bash
dotnet add package EventLoom.EntityFrameworkCore.Sqlite --prerelease
```

Configure it through the hosting API:

```csharp
using EventLoom;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;

builder.Services.AddEventLoom(eventLoom => eventLoom
    .RegisterEvent<OrderPlaced>()
    .UseSqlite("Data Source=eventloom.db"));
```

The package restores the core, EF Core, and hosting packages transitively, but
applications should directly reference `EventLoom.Hosting` because it provides
their composition, worker, health-check, and telemetry APIs. SQLite does not
support distributed workers or multiple application instances against one event
store. Use
`EventLoom.EntityFrameworkCore.PostgreSql` for production distributed
deployments.

Packages are currently pre-release and not published to NuGet. See the
[EF Core configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md)
for provider setup and migration boundaries.
