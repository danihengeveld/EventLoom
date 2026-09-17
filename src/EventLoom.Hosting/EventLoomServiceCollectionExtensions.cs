using System.Reflection;
using System.Text.Json.Serialization;
using EventLoom;
using EventLoom.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Hosting;

/// <summary>
/// Adds EventLoom services to an application's dependency-injection container.
/// </summary>
public static class EventLoomServiceCollectionExtensions
{
    /// <summary>
    /// Adds EventLoom's event registry, serializer, event store, clock, and EF Core context.
    /// Events and a database provider must be explicitly configured through <paramref name="configure"/>.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Configures event registrations, serialization, storage, and aggregate repositories.</param>
    /// <returns>The configured service collection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddEventLoom(
        this IServiceCollection services,
        Action<EventLoomBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new EventLoomBuilder(services);
        configure(builder);
        builder.RegisterServices();
        return services;
    }
}

/// <summary>
/// Configures EventLoom services using explicit event and storage registrations.
/// </summary>
public sealed class EventLoomBuilder
{
    private readonly IServiceCollection services;
    private readonly EventRegistry registry = new();
    private readonly List<JsonSerializerContext> serializerContexts = [];
    private readonly List<IEventUpcaster> upcasters = [];
    private EventStoreOptions eventStoreOptions = new();
    private TimeProvider timeProvider = TimeProvider.System;
    private Action<IServiceProvider, DbContextOptionsBuilder>? configureDbContext;

    internal EventLoomBuilder(IServiceCollection services)
    {
        this.services = services;
    }

    /// <summary>
    /// Registers a domain-event type for persistence and deserialization.
    /// </summary>
    /// <typeparam name="TEvent">The concrete domain-event type to register.</typeparam>
    /// <returns>This builder.</returns>
    public EventLoomBuilder RegisterEvent<TEvent>()
        where TEvent : IDomainEvent
    {
        registry.RegisterEvent<TEvent>();
        return this;
    }

    /// <summary>
    /// Registers all concrete domain-event types in an assembly.
    /// </summary>
    /// <param name="assembly">The assembly containing the event types.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is <see langword="null"/>.</exception>
    public EventLoomBuilder RegisterEventsFromAssembly(Assembly assembly)
    {
        registry.RegisterAssembly(assembly);
        return this;
    }

    /// <summary>
    /// Adds a source-generated JSON serialization context for event serialization.
    /// </summary>
    /// <param name="context">The serialization context to use.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public EventLoomBuilder AddJsonSerializerContext(JsonSerializerContext context)
    {
        serializerContexts.Add(context ?? throw new ArgumentNullException(nameof(context)));
        return this;
    }

    /// <summary>
    /// Adds an event upcaster used when reading earlier versions of an event.
    /// </summary>
    /// <param name="upcaster">The upcaster to register.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="upcaster"/> is <see langword="null"/>.</exception>
    public EventLoomBuilder AddUpcaster(IEventUpcaster upcaster)
    {
        upcasters.Add(upcaster ?? throw new ArgumentNullException(nameof(upcaster)));
        return this;
    }

    /// <summary>
    /// Configures the names and schema used by the EventLoom event-store tables.
    /// </summary>
    /// <param name="configure">Configures the event-store options.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    public EventLoomBuilder ConfigureEventStore(Action<EventStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(eventStoreOptions);
        return this;
    }

    /// <summary>
    /// Configures tenancy enforcement for event-store operations.
    /// When required, a scoped <see cref="ITenantAccessor"/> must provide a tenant and
    /// explicit tenant arguments must match it.
    /// </summary>
    /// <param name="mode">The tenancy enforcement mode.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder ConfigureTenancy(TenancyMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        eventStoreOptions.TenancyMode = mode;
        return this;
    }

    /// <summary>
    /// Uses the supplied time provider for persisted event timestamps and the EventLoom clock.
    /// </summary>
    /// <param name="provider">The time provider to use.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    public EventLoomBuilder UseTimeProvider(TimeProvider provider)
    {
        timeProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        return this;
    }

    /// <summary>
    /// Registers a typed aggregate repository.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <typeparam name="TId">The aggregate identifier type.</typeparam>
    /// <param name="factory">Creates an aggregate for its identifier.</param>
    /// <param name="pendingEvents">Gets the aggregate's uncommitted events.</param>
    /// <param name="version">Gets the aggregate's current version.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder AddAggregateRepository<TAggregate, TId>(
        Func<TId, TAggregate> factory,
        Func<TAggregate, IEnumerable<IDomainEvent>> pendingEvents,
        Func<TAggregate, long> version)
        where TAggregate : Aggregate<TId>
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(pendingEvents);
        ArgumentNullException.ThrowIfNull(version);

        services.AddScoped(services => new AggregateRepository<TAggregate, TId>(
            services.GetRequiredService<EventStore>(),
            factory,
            pendingEvents,
            version));
        return this;
    }

    /// <summary>
    /// Registers a typed aggregate repository using the standard
    /// <see cref="Aggregate{TId}.PendingEvents"/> and
    /// <see cref="Aggregate{TId}.Version"/> members.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <typeparam name="TId">The aggregate identifier type.</typeparam>
    /// <param name="factory">Creates an aggregate for its identifier.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder AddAggregateRepository<TAggregate, TId>(
        Func<TId, TAggregate> factory)
        where TAggregate : Aggregate<TId>
    {
        ArgumentNullException.ThrowIfNull(factory);
        return AddAggregateRepository(
            factory,
            aggregate => aggregate.PendingEvents.Select(value => value.Event),
            aggregate => aggregate.Version);
    }

    /// <summary>
    /// Configures the EF Core options for the EventLoom event-store context.
    /// </summary>
    /// <param name="configure">Configures the context options, including its database provider.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A database provider has already been configured.</exception>
    public EventLoomBuilder ConfigureDbContext(Action<DbContextOptionsBuilder> configure)
    {
        if (configureDbContext is not null)
        {
            throw new InvalidOperationException("An EventLoom database provider has already been configured.");
        }

        ArgumentNullException.ThrowIfNull(configure);
        configureDbContext = (_, options) => configure(options);
        return this;
    }

    internal void RegisterServices()
    {
        if (configureDbContext is null)
        {
            throw new InvalidOperationException(
                "Configure an EventLoom database provider with a provider-specific UsePostgreSql or UseSqlite extension.");
        }

        var upcasterChains = upcasters
            .GroupBy(upcaster => upcaster.EventName, StringComparer.Ordinal)
            .Select(group => new EventUpcasterChain(group.Key, group))
            .ToArray();

        services.AddSingleton(registry);
        services.AddSingleton(new EventSerializer(registry, serializerContexts, upcasterChains: upcasterChains));
        services.AddSingleton<IEventIdGenerator, UuidV7EventIdGenerator>();
        services.AddSingleton(timeProvider);
        services.AddSingleton<TimeProviderClock>();
        services.AddSingleton(eventStoreOptions);
        services.AddDbContext<EventStoreDbContext>((serviceProvider, options) =>
        {
            configureDbContext(serviceProvider, options);
        });
        services.AddScoped<EventStore>();
        services.AddScoped<WorkerLeaseStore>();
    }
}
