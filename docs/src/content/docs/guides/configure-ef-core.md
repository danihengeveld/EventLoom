---
title: Configure the EF Core event store
description: Register and migrate EventLoom's Phase 3 EF Core event store.
---

This guide configures the dedicated event-store context. Keep it separate from your application's `DbContext` so event-store migrations and transactions remain independently manageable.

## Register the context

For PostgreSQL:

```csharp
builder.Services.AddDbContext<EventStoreDbContext>((services, options) =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("EventStore"),
        npgsql => npgsql.MigrationsAssembly(EventStoreSchema.MigrationsAssemblyName));
});
```

For SQLite, use `UseSqlite` and set `UseSchema` to `false` (the default):

```csharp
var eventStoreOptions = new EventStoreOptions
{
    TablePrefix = "eventloom_",
    UseSchema = false
};

builder.Services.AddSingleton(eventStoreOptions);
builder.Services.AddDbContext<EventStoreDbContext>((_, options) =>
    options.UseSqlite("Data Source=eventloom.db"));
```

Register the same `EventStoreOptions` instance with dependency injection when customizing the table prefix or enabling a provider-supported schema. The context receives it through its optional constructor parameter. PostgreSQL supports schemas; SQLite does not.

## Apply migrations

Run the event-store migration during deployment or application startup:

```csharp
await EventStoreSchema.MigrateAsync(
    dbContext,
    cancellationToken);
```

`EventStoreSchema.GetTableNames(options)` can be used by diagnostics or health checks to list the tables managed by EventLoom. Do not use it as a substitute for applying migrations.

## Provider capabilities

Use `IEventStoreProviderCapabilities` to keep provider-specific behavior explicit. `PostgreSqlProviderCapabilities` reports support for schemas and distributed workers. `SqliteProviderCapabilities` reports neither and throws `DistributedWorkerConfigurationException` if distributed workers are enabled.
