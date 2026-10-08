using EventLoom.Hosting;
using EventLoom.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
                    [HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
                    [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
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
        IStorageSchema schema,
        CancellationToken cancellationToken)
    {
        var validation = await schema.ValidateAsync(cancellationToken);
        return Results.Ok(new
        {
            validation.IsCompatible,
            validation.CanConnect,
            validation.MissingCount,
            validation.IncompatibleCount
        });
    }

    private static async Task<IResult> GetDiagnosticsAsync(
        IStorageSchema storageSchema,
        EventLoomOperationalDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var schema = await storageSchema.ValidateAsync(cancellationToken);
        var operational = await diagnostics.GetAsync(cancellationToken);
        return Results.Ok(new
        {
            SchemaCompatible = schema.IsCompatible,
            schema.CanConnect,
            schema.MissingCount,
            schema.IncompatibleCount,
            operational.Projections.ProjectionCount,
            operational.Projections.UnresolvedFailureCount,
            operational.Projections.MaximumLag,
            operational.Outbox.PendingMessageCount
        });
    }
}
