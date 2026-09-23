using EventLoom.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EventLoom.Hosting;

/// <summary>Configures EventLoom operational health-check thresholds.</summary>
public sealed class EventLoomHealthCheckOptions
{
    /// <summary>Gets or sets the largest acceptable event-offset lag for an asynchronous projection.</summary>
    public long MaximumProjectionLag { get; set; } = 1_000;

    /// <summary>Gets or sets the largest acceptable unpublished outbox backlog.</summary>
    public int MaximumOutboxBacklog { get; set; } = 1_000;

    internal void Validate()
    {
        if (MaximumProjectionLag < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumProjectionLag));
        }

        if (MaximumOutboxBacklog < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutboxBacklog));
        }
    }
}

/// <summary>Adds health checks for EventLoom storage, projections, and outbox delivery.</summary>
public static class EventLoomHealthCheckServiceCollectionExtensions
{
    /// <summary>
    /// Adds payload-safe health checks for EventLoom after <see cref="EventLoomServiceCollectionExtensions.AddEventLoom(IServiceCollection)"/>
    /// has configured the event store.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optionally configures projection-lag and outbox-backlog thresholds.</param>
    /// <returns>The configured service collection.</returns>
    public static IServiceCollection AddEventLoomHealthChecks(
        this IServiceCollection services,
        Action<EventLoomHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new EventLoomHealthCheckOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddHealthChecks()
            .AddCheck<EventStoreHealthCheck>("eventloom.event-store", tags: ["eventloom", "ready"])
            .AddCheck<ProjectionHealthCheck>("eventloom.projections", tags: ["eventloom", "ready"])
            .AddCheck<OutboxHealthCheck>("eventloom.outbox", tags: ["eventloom", "ready"]);
        return services;
    }
}

internal sealed class EventStoreHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var eventStoreContext = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
        try
        {
            if (!await eventStoreContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                return HealthCheckResult.Unhealthy("EventLoom event-store connectivity failed.");
            }

            var validation = await EventStoreSchema.ValidateAsync(eventStoreContext, cancellationToken)
                .ConfigureAwait(false);
            return validation.IsCompatible
                ? HealthCheckResult.Healthy("EventLoom event-store schema is compatible.")
                : HealthCheckResult.Unhealthy(
                    "EventLoom event-store schema is incompatible.",
                    data: new Dictionary<string, object>
                    {
                        ["missing_table_count"] = validation.MissingTables.Count,
                        ["missing_column_count"] = validation.MissingColumns.Count,
                        ["incompatible_column_count"] = validation.IncompatibleColumns.Count
                    });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("EventLoom event-store connectivity or schema validation failed.");
        }
    }
}

internal sealed class ProjectionHealthCheck(
    IServiceScopeFactory scopeFactory,
    ProjectionRegistry registry,
    EventLoomHealthCheckOptions options) : IHealthCheck
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly ProjectionRegistry registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly EventLoomHealthCheckOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var summary = await scope.ServiceProvider.GetRequiredService<ProjectionStore>()
                .GetHealthSummaryAsync(registry.AsynchronousProjections, cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["projection_count"] = summary.ProjectionCount,
                ["unresolved_failure_count"] = summary.UnresolvedFailureCount,
                ["maximum_lag"] = summary.MaximumLag
            };
            if (summary.UnresolvedFailureCount > 0)
            {
                return HealthCheckResult.Unhealthy(
                    "EventLoom has unresolved projection failures.",
                    data: data);
            }

            return summary.MaximumLag > options.MaximumProjectionLag
                ? HealthCheckResult.Degraded("EventLoom projection lag exceeds its configured threshold.", data: data)
                : HealthCheckResult.Healthy("EventLoom projections are current.", data: data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("EventLoom projection health query failed.");
        }
    }
}

internal sealed class OutboxHealthCheck(
    IServiceScopeFactory scopeFactory,
    EventLoomHealthCheckOptions options) : IHealthCheck
{
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly EventLoomHealthCheckOptions options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var summary = await scope.ServiceProvider.GetRequiredService<OutboxStore>()
                .GetHealthSummaryAsync(cancellationToken).ConfigureAwait(false);
            var data = new Dictionary<string, object>
            {
                ["pending_message_count"] = summary.PendingMessageCount
            };
            return summary.PendingMessageCount > options.MaximumOutboxBacklog
                ? HealthCheckResult.Degraded("EventLoom outbox backlog exceeds its configured threshold.", data: data)
                : HealthCheckResult.Healthy("EventLoom outbox backlog is within its configured threshold.", data: data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("EventLoom outbox health query failed.");
        }
    }
}
