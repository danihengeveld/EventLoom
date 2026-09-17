using EventLoom;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using EventLoom.Ordering.Api;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantAccessor, RequestTenantAccessor>();

var provider = builder.Configuration["EVENTLOOM_DATABASE_PROVIDER"]?.Trim().ToLowerInvariant() ?? "postgres";
var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? (provider == "sqlite"
        ? "Data Source=eventloom-ordering.db"
        : "Host=localhost;Database=eventloom;Username=eventloom;******");

builder.Services.AddEventLoom(eventLoom =>
{
    eventLoom.AddOrdering();
    switch (provider)
    {
        case "sqlite":
            eventLoom.UseSqlite(connectionString);
            break;
        case "postgres":
            eventLoom.UsePostgreSql(connectionString);
            break;
        default:
            throw new InvalidOperationException("EVENTLOOM_DATABASE_PROVIDER must be 'postgres' or 'sqlite'.");
    }
});
builder.Services.AddEventLoomHealthChecks(options =>
{
    options.MaximumProjectionLag = 500;
    options.MaximumOutboxBacklog = 500;
});

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<EventStoreDbContext>().Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseWhen(
    context => !app.Environment.IsDevelopment() ||
        (!context.Request.Path.StartsWithSegments("/openapi") &&
         !context.Request.Path.StartsWithSegments("/scalar")),
    branch => branch.Use(TenantRequirementMiddleware.InvokeAsync));
app.MapHealthChecks("/health");
app.MapOrderEndpoints();
app.Run();
