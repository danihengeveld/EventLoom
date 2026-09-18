using EventLoom;
using EventLoom.AspNetCore;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
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
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Tenant"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Tenant-ID",
            Description = "Tenant used to scope EventLoom requests."
        };
        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Tenant")] = []
        });

        return Task.CompletedTask;
    }));
builder.Services.AddHttpContextAccessor();

var connectionString = builder.Configuration.GetConnectionString("EventStore")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:EventStore is required. Run the sample through EventLoom.Ordering.AppHost.");

builder.Services
    .AddEventLoom()
    .UsePostgreSql(connectionString)
    .AddOrdering();
builder.Services.AddEventLoomHealthChecks(options =>
{
    options.MaximumProjectionLag = 500;
    options.MaximumOutboxBacklog = 500;
});

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    await app.InitializeEventLoomDevelopmentDatabaseAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.AddPreferredSecuritySchemes("Tenant");
        options.AddApiKeyAuthentication("Tenant", scheme =>
            scheme.WithName("X-Tenant-ID").WithValue("acme"));
    });
}

app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"),
    branch => branch.Use(TenantRequirementMiddleware.InvokeAsync));
app.MapDefaultEndpoints();
app.MapEventLoomHealthChecks("/health/eventloom");
app.MapOrderEndpoints();
app.Run();
