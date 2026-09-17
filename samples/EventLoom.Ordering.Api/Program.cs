using EventLoom;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;
using EventLoom.Ordering.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using System.Text.Json.Serialization;
using EventLoom.Ordering.Api.Api;
using EventLoom.Ordering.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddEventLoomInstrumentation())
    .WithTracing(tracing => tracing.AddEventLoomInstrumentation());
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantAccessor, RequestTenantAccessor>();

var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:EventStore is required. Run the sample through EventLoom.Ordering.AppHost.");

builder.Services.AddEventLoom(eventLoom =>
{
    eventLoom.AddOrdering();
    eventLoom.UsePostgreSql(connectionString);
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
    context =>
        !context.Request.Path.StartsWithSegments("/health") &&
        !context.Request.Path.StartsWithSegments("/alive") &&
        (!app.Environment.IsDevelopment() ||
         (!context.Request.Path.StartsWithSegments("/openapi") &&
          !context.Request.Path.StartsWithSegments("/scalar"))),
    branch => branch.Use(TenantRequirementMiddleware.InvokeAsync));
app.MapDefaultEndpoints();
app.MapOrderEndpoints();
app.Run();
