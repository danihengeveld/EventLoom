# EventLoom.AspNetCore

`EventLoom.AspNetCore` is the canonical ASP.NET Core application package for
EventLoom. It brings in hosting composition, workers, health checks, and
endpoint conventions.

Install it alongside exactly one EventLoom storage provider:

```bash
dotnet add package EventLoom.AspNetCore --prerelease
```

Register EventLoom and its health checks during startup, then map the tagged
EventLoom checks:

```csharp
using EventLoom.AspNetCore;
using EventLoom.Hosting;

builder.Services
    .AddEventLoom()
    .UsePostgreSql(connectionString)
    .AddAggregate<Order, Guid>(aggregate => aggregate
        .ConstructWith(id => new Order(id))
        .UseStream("order", id => id.ToString("D")));
builder.Services.AddEventLoomHealthChecks();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    await app.InitializeEventLoomDevelopmentDatabaseAsync();
}

app.MapEventLoomHealthChecks();
```

The default endpoint is `/health`; pass a different route pattern when needed.
The development initializer creates only an empty database and refuses to run
outside Development. Production schema evolution remains application-owned
through reviewed, deployment-managed migrations (EF Core providers) or index and
collection management (MongoDB).

For a protected, payload-safe operational summary, opt in explicitly with an
existing named authorization policy:

```csharp
app.MapEventLoomAdminDiagnostics("EventLoomOperators");
```

This maps `/admin/eventloom/schema` and `/admin/eventloom/diagnostics`. Mapping
requires a non-empty named policy and therefore refuses anonymous or
fallback-policy-only administration. Both endpoints return aggregate counts and
schema compatibility state only (`CanConnect`, `MissingCount`, and
`IncompatibleCount`, reported by the provider's `IStorageSchema`); they never expose tenants, event IDs, stream
IDs, payloads, metadata, or exception details.

EventLoom is currently pre-release. See the
[configuration guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/configure-ef-core.md),
[observability guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/observability.md),
and [production deployment guide](https://github.com/danihengeveld/EventLoom/blob/main/docs/src/content/docs/guides/production-deployment.md)
for application setup and operational guidance.
