using EventLoom.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Hosting;

internal sealed class ProjectionRegistry(IEnumerable<ProjectionHandlerRegistration> registrations)
{
    private readonly IReadOnlyList<ProjectionHandlerRegistration> registrations = Validate(registrations);

    public IReadOnlyList<ProjectionKey> AsynchronousProjections { get; } = registrations
        .Where(value => value.Mode == ProjectionMode.Asynchronous)
        .Select(value => value.Key)
        .Distinct()
        .OrderBy(value => value.Name, StringComparer.Ordinal)
        .ThenBy(value => value.Version)
        .ToArray();

    public async Task DispatchAsynchronousAsync(
        ProjectionKey key,
        IServiceProvider serviceProvider,
        EventEnvelope envelope,
        EventStoreDbContext context,
        CancellationToken cancellationToken)
    {
        foreach (var registration in registrations.Where(value =>
                     value.Mode == ProjectionMode.Asynchronous &&
                     value.Key == key &&
                     value.EventType.IsInstanceOfType(envelope.Event)))
        {
            await registration.DispatchAsync(serviceProvider, envelope, context, cancellationToken);
        }
    }

    internal async Task DispatchInlineAsync(
        IServiceProvider serviceProvider,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        foreach (var registration in registrations.Where(value =>
                     value.Mode == ProjectionMode.Inline &&
                     value.EventType.IsInstanceOfType(envelope.Event)))
        {
            await registration.DispatchAsync(
                serviceProvider,
                envelope,
                null,
                cancellationToken);
        }
    }

    private static IReadOnlyList<ProjectionHandlerRegistration> Validate(
        IEnumerable<ProjectionHandlerRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var materialized = registrations.ToArray();
        foreach (var registration in materialized)
        {
            registration.Key.Validate();
        }

        if (materialized.GroupBy(value => new
            {
                value.Mode,
                value.Key,
                value.EventType,
                value.HandlerType
            }).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException(
                "A projection handler can only be registered once per event type and mode.");
        }

        return materialized;
    }
}

internal enum ProjectionMode
{
    Asynchronous,
    Inline
}

internal sealed record ProjectionHandlerRegistration(
    ProjectionKey Key,
    ProjectionMode Mode,
    Type EventType,
    Type HandlerType,
    Func<IServiceProvider?, EventEnvelope, EventStoreDbContext?, CancellationToken, Task> DispatchAsync)
{
    public static ProjectionHandlerRegistration CreateAsynchronous<THandler, TEvent>(ProjectionKey key)
        where THandler : class, IProjectionHandler<TEvent> =>
        new(
            key,
            ProjectionMode.Asynchronous,
            typeof(TEvent),
            typeof(THandler),
            static async (services, envelope, _, cancellationToken) =>
            {
                var handler = services?.GetRequiredService<THandler>()
                              ?? throw new InvalidOperationException(
                                  "Asynchronous projection dispatch requires a service provider.");
                await handler.HandleAsync(ToTyped<TEvent>(envelope), cancellationToken);
            });

    public static ProjectionHandlerRegistration CreateEf<THandler, TEvent>(ProjectionKey key)
        where THandler : class, IEfProjectionHandler<TEvent> =>
        new(
            key,
            ProjectionMode.Asynchronous,
            typeof(TEvent),
            typeof(THandler),
            static async (services, envelope, context, cancellationToken) =>
            {
                var handler = services?.GetRequiredService<THandler>()
                              ?? throw new InvalidOperationException(
                                  "EF projection dispatch requires a service provider.");
                await handler.HandleAsync(
                    ToTyped<TEvent>(envelope),
                    context ?? throw new InvalidOperationException(
                        "EF projection dispatch requires an EventLoom context."),
                    cancellationToken);
            });

    public static ProjectionHandlerRegistration CreateInline<THandler, TEvent>(ProjectionKey key)
        where THandler : class, IInlineProjectionHandler<TEvent> =>
        new(
            key,
            ProjectionMode.Inline,
            typeof(TEvent),
            typeof(THandler),
            static async (services, envelope, _, cancellationToken) =>
            {
                var handler = services?.GetRequiredService<THandler>()
                              ?? throw new InvalidOperationException(
                                  "Inline projection dispatch requires a service provider.");
                await handler.HandleAsync(ToTyped<TEvent>(envelope), cancellationToken);
            });

    private static EventEnvelope<TEvent> ToTyped<TEvent>(EventEnvelope envelope) =>
        envelope.Event is TEvent @event
            ? new EventEnvelope<TEvent>(
                envelope.EventId,
                envelope.EventType,
                envelope.EventTypeVersion,
                envelope.StreamId,
                envelope.AggregateType,
                envelope.StreamVersion,
                envelope.TenantOffset,
                envelope.TenantId,
                envelope.OccurredAt,
                @event,
                envelope.Metadata)
            : throw new InvalidOperationException(
                $"Event '{envelope.EventType}' is not compatible with projection handler '{typeof(TEvent).FullName}'.");
}

internal sealed class InlineProjectionDispatcher(ProjectionRegistry registry, IServiceProvider serviceProvider)
    : IInlineProjectionDispatcher
{
    private readonly ProjectionRegistry registry = registry ?? throw new ArgumentNullException(nameof(registry));

    private readonly IServiceProvider serviceProvider =
        serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

    public Task DispatchAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return registry.DispatchInlineAsync(serviceProvider, envelope, cancellationToken);
    }
}
