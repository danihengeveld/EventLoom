using EventLoom.EntityFrameworkCore;
using EventLoom.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventLoom.AspNetCore;

/// <summary>Provides ASP.NET Core endpoint conventions for EventLoom.</summary>
public static class EventLoomEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the EventLoom health checks, including event-store, projection, and outbox checks,
    /// to an ASP.NET Core endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <param name="pattern">The route pattern for the health-check endpoint.</param>
    /// <returns>The endpoint convention builder for the mapped health checks.</returns>
    public static IEndpointConventionBuilder MapEventLoomHealthChecks(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/health")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapHealthChecks(
            pattern,
            new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("eventloom", StringComparer.OrdinalIgnoreCase),
                ResultStatusCodes =
                {
                    [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                    [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
                }
            });
    }

    /// <summary>
    /// Maps protected, payload-safe EventLoom operational diagnostics.
    /// </summary>
    /// <remarks>
    /// This endpoint is opt-in and requires a named authorization policy. It exposes
    /// aggregate counts and compatibility state only; it never returns tenant IDs,
    /// event identifiers, payloads, metadata, or exception details.
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to configure.</param>
    /// <param name="authorizationPolicy">The required named authorization policy.</param>
    /// <param name="pattern">The base route pattern for the administrative endpoints.</param>
    /// <returns>The route group containing the protected diagnostics endpoints.</returns>
    /// <exception cref="ArgumentException"><paramref name="authorizationPolicy"/> or <paramref name="pattern"/> is empty.</exception>
    public static RouteGroupBuilder MapEventLoomAdminDiagnostics(
        this IEndpointRouteBuilder endpoints,
        string authorizationPolicy,
        string pattern = "/admin/eventloom")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var group = endpoints.MapGroup(pattern).RequireAuthorization(authorizationPolicy);
        group.MapGet("/schema", GetSchemaDiagnosticsAsync);
        group.MapGet("/diagnostics", GetDiagnosticsAsync);
        return group;
    }

    private static async Task<IResult> GetSchemaDiagnosticsAsync(
        EventStoreDbContext context,
        CancellationToken cancellationToken)
    {
        var validation = await EventStoreSchema.ValidateAsync(context, cancellationToken);
        return Results.Ok(new
        {
            validation.IsCompatible,
            MissingTableCount = validation.MissingTables.Count,
            MissingColumnCount = validation.MissingColumns.Count,
            IncompatibleColumnCount = validation.IncompatibleColumns.Count
        });
    }

    private static async Task<IResult> GetDiagnosticsAsync(
        EventStoreDbContext context,
        EventLoomOperationalDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var schema = await EventStoreSchema.ValidateAsync(context, cancellationToken);
        var operational = await diagnostics.GetAsync(cancellationToken);
        return Results.Ok(new
        {
            SchemaCompatible = schema.IsCompatible,
            MissingTableCount = schema.MissingTables.Count,
            MissingColumnCount = schema.MissingColumns.Count,
            IncompatibleColumnCount = schema.IncompatibleColumns.Count,
            operational.Projections.ProjectionCount,
            operational.Projections.UnresolvedFailureCount,
            operational.Projections.MaximumLag,
            operational.Outbox.PendingMessageCount
        });
    }
}
