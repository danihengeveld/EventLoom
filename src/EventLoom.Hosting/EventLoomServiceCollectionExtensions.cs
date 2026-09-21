using System.Reflection;
using EventLoom.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace EventLoom.Hosting;

/// <summary>
/// Adds EventLoom services to an application's dependency-injection container.
/// </summary>
public static class EventLoomServiceCollectionExtensions
{
    /// <summary>Adds EventLoom and returns its fluent configuration builder.</summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The EventLoom configuration builder.</returns>
    public static EventLoomBuilder AddEventLoom(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        var builder = new EventLoomBuilder(services);
        builder.RegisterServices();
        return builder;
    }

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

        var builder = services.AddEventLoom();
        configure(builder);
        builder.ValidateConfiguration();
        return services;
    }
}

/// <summary>
/// Configures EventLoom services using explicit event and storage registrations.
/// </summary>
public sealed partial class EventLoomBuilder
{
    private readonly IServiceCollection services;
    private readonly EventRegistry registry = new();
    private readonly List<IEventUpcaster> upcasters = [];
    private readonly List<ProjectionHandlerRegistration> projectionRegistrations = [];
    private readonly List<Action<ModelBuilder>> projectionModelConfigurations = [];
    private EventStoreOptions eventStoreOptions = new();
    private EventStoreWorkerOptions workerOptions = new();
    private readonly OutboxOptions outboxOptions = new();
    private ISnapshotRetentionPolicy snapshotRetentionPolicy = new KeepLatestSnapshotsPolicy(1);
    private TimeProvider timeProvider = TimeProvider.System;
    private Action<IServiceProvider, DbContextOptionsBuilder>? configureDbContext;
    private bool outboxPublisherRegistered;
    private bool servicesRegistered;
    private ServiceDescriptor? singleTenantAccessorDescriptor;

    internal EventLoomBuilder(IServiceCollection services)
    {
        this.services = services;
    }

    /// <summary>
    /// Registers a domain-event type for persistence and deserialization.
    /// </summary>
    /// <typeparam name="TEvent">The concrete domain-event type to register.</typeparam>
    /// <returns>This builder.</returns>
    public EventLoomBuilder AddEvent<TEvent>()
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
    public EventLoomBuilder AddEventsFromAssembly(Assembly assembly)
    {
        registry.RegisterAssembly(assembly);
        return this;
    }

    /// <summary>Registers concrete domain-event types from the assembly containing <typeparamref name="TMarker"/>.</summary>
    public EventLoomBuilder AddEventsFromAssemblyContaining<TMarker>() =>
        AddEventsFromAssembly(typeof(TMarker).Assembly);

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

    /// <summary>Uses one tenant for normal repository operations.</summary>
    /// <param name="tenantId">The stable persisted tenant identifier.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder UseSingleTenancy(string tenantId = "default")
    {
        eventStoreOptions.TenancyMode = TenancyMode.SingleTenant;
        eventStoreOptions.SingleTenantId = new TenantId(tenantId).Value;
        return this;
    }

    /// <summary>Enables request-scoped multi-tenancy using the specified accessor.</summary>
    /// <typeparam name="TAccessor">The scoped tenant accessor implementation.</typeparam>
    /// <returns>This builder.</returns>
    public EventLoomBuilder UseMultiTenancy<TAccessor>()
        where TAccessor : class, ITenantAccessor
    {
        services.AddScoped<ITenantAccessor, TAccessor>();
        return UseMultiTenancy();
    }

    /// <summary>Enables multi-tenancy using an <see cref="ITenantAccessor"/> registered by the application.</summary>
    /// <returns>This builder.</returns>
    public EventLoomBuilder UseMultiTenancy()
    {
        eventStoreOptions.TenancyMode = TenancyMode.MultiTenant;
        return this;
    }

    /// <summary>
    /// Configures projection worker identity, polling, lease, and retry settings. Outbox worker
    /// settings are configured separately through
    /// <see cref="AddOutboxPublisher{TPublisher}(Action{OutboxOptions}?)"/>.
    /// </summary>
    public EventLoomBuilder ConfigureWorkers(Action<EventStoreWorkerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(workerOptions);
        workerOptions.Validate();
        return this;
    }

    /// <summary>Configures how many recent snapshots are retained for each aggregate stream.</summary>
    /// <param name="retentionPolicy">The policy to apply whenever EventLoom persists a snapshot.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder ConfigureSnapshotRetention(ISnapshotRetentionPolicy retentionPolicy)
    {
        snapshotRetentionPolicy = retentionPolicy ?? throw new ArgumentNullException(nameof(retentionPolicy));
        return this;
    }

    /// <summary>Adds read-model mappings to the EventLoom context used by transactional projections.</summary>
    /// <param name="configure">Configures one or more EF Core read-model entity mappings.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder ConfigureProjectionModel(Action<ModelBuilder> configure)
    {
        projectionModelConfigurations.Add(configure ?? throw new ArgumentNullException(nameof(configure)));
        return this;
    }

    /// <summary>Registers an asynchronous, at-least-once typed projection handler.</summary>
    /// <typeparam name="TProjection">The projection handler type.</typeparam>
    /// <typeparam name="TEvent">The event type handled by the projection.</typeparam>
    /// <param name="name">The stable projection name.</param>
    /// <param name="version">The positive projection version and checkpoint namespace.</param>
    /// <returns>This builder.</returns>
    internal EventLoomBuilder AddProjection<TProjection, TEvent>(string name, int version = 1)
        where TProjection : class, IProjectionHandler<TEvent>
    {
        var key = new ProjectionKey(name, version);
        key.Validate();
        services.TryAddScoped<TProjection>();
        projectionRegistrations.Add(ProjectionHandlerRegistration.CreateAsynchronous<TProjection, TEvent>(key));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProjectionWorker>());
        return this;
    }

    /// <summary>
    /// Registers a typed projection handler whose read-model changes and checkpoint commit in one EF Core transaction.
    /// </summary>
    /// <typeparam name="TProjection">The projection handler type.</typeparam>
    /// <typeparam name="TEvent">The event type handled by the projection.</typeparam>
    /// <param name="name">The stable projection name.</param>
    /// <param name="version">The positive projection version and checkpoint namespace.</param>
    /// <returns>This builder.</returns>
    internal EventLoomBuilder AddEfProjection<TProjection, TEvent>(string name, int version = 1)
        where TProjection : class, IEfProjectionHandler<TEvent>
    {
        var key = new ProjectionKey(name, version);
        key.Validate();
        services.TryAddScoped<TProjection>();
        projectionRegistrations.Add(ProjectionHandlerRegistration.CreateEf<TProjection, TEvent>(key));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ProjectionWorker>());
        return this;
    }

    /// <summary>Registers a typed projection handler that executes inside the event append transaction.</summary>
    /// <typeparam name="TProjection">The inline projection handler type.</typeparam>
    /// <typeparam name="TEvent">The event type handled by the projection.</typeparam>
    /// <param name="name">The stable inline projection name.</param>
    /// <param name="version">The positive inline projection version.</param>
    /// <returns>This builder.</returns>
    internal EventLoomBuilder AddInlineProjection<TProjection, TEvent>(string name, int version = 1)
        where TProjection : class, IInlineProjectionHandler<TEvent>
    {
        var key = new ProjectionKey(name, version);
        key.Validate();
        services.TryAddScoped<TProjection>();
        projectionRegistrations.Add(ProjectionHandlerRegistration.CreateInline<TProjection, TEvent>(key));
        services.TryAddScoped<IInlineProjectionDispatcher, InlineProjectionDispatcher>();
        return this;
    }

    /// <summary>
    /// Registers the provider-specific retry policy used by event-store appends.
    /// </summary>
    /// <typeparam name="TPolicy">The retry policy implementation.</typeparam>
    /// <returns>This builder.</returns>
    public EventLoomBuilder AddEventStoreRetryPolicy<TPolicy>()
        where TPolicy : class, IEventStoreRetryPolicy
    {
        services.AddScoped<IEventStoreRetryPolicy, TPolicy>();
        return this;
    }

    /// <summary>
    /// Registers the single transport-neutral publisher that delivers durable outbox messages, and
    /// optionally configures its worker: instance identity, polling, batching, lease, retry, and
    /// successful-delivery retention.
    /// Registering a publisher enables transactional outbox message creation and starts the outbox
    /// worker. EventLoom calls the publisher at least once and supplies
    /// <see cref="OutboxMessage.MessageId"/> as a stable idempotency key for the transport.
    /// </summary>
    /// <typeparam name="TPublisher">The publisher implementation.</typeparam>
    /// <param name="configure">Optionally configures the outbox worker and lifecycle.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">More than one publisher is registered.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A configured outbox value is out of range.</exception>
    public EventLoomBuilder AddOutboxPublisher<TPublisher>(Action<OutboxOptions>? configure = null)
        where TPublisher : class, IOutboxPublisher
    {
        if (outboxPublisherRegistered)
        {
            throw new InvalidOperationException("Only one EventLoom outbox publisher can be registered.");
        }

        configure?.Invoke(outboxOptions);
        outboxOptions.Validate();
        services.AddScoped<IOutboxPublisher, TPublisher>();
        outboxPublisherRegistered = true;
        eventStoreOptions.OutboxEnabled = true;
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, OutboxPublisherWorker>());
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

    /// <summary>Registers an aggregate using one cohesive persistence configuration.</summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <typeparam name="TId">The aggregate identifier type.</typeparam>
    /// <param name="configure">Configures aggregate construction, stream identity, and optional snapshots.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder AddAggregate<TAggregate, TId>(
        Action<AggregateRegistrationBuilder<TAggregate, TId>> configure)
        where TAggregate : Aggregate<TId>
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AggregateRegistrationBuilder<TAggregate, TId>();
        configure(builder);
        var registration = builder.Build();

        var snapshots = registration.SnapshotConfiguration;
        services.AddScoped(serviceProvider => new AggregateRepository<TAggregate, TId>(
            serviceProvider.GetRequiredService<EventStore>(),
            registration.Factory,
            registration.AggregateType,
            registration.StreamId,
            ResolveTenantAccessor(serviceProvider),
            snapshots is null ? null : serviceProvider.GetRequiredService<SnapshotStore>(),
            snapshots?.SnapshotType,
            snapshots?.Policy,
            snapshots?.Invalidator,
            snapshots?.RetentionPolicy,
            snapshots?.Upcasters));
        return this;
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

    /// <summary>
    /// Configures the EF Core options for the EventLoom event-store context using scoped services.
    /// </summary>
    /// <remarks>
    /// This advanced overload is intended for sharing a scoped <c>DbConnection</c> with an
    /// application context so that it can share a unit of work with EventLoom through
    /// <see cref="EventStore.BeginUnitOfWorkAsync"/>, or through the lower-level
    /// <see cref="EventStore.AppendInTransactionAsync"/> for callers that manage a
    /// <see cref="System.Data.Common.DbTransaction"/> directly.
    /// </remarks>
    /// <param name="configure">Configures the context options, including its database provider.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A database provider has already been configured.</exception>
    public EventLoomBuilder ConfigureDbContext(
        Action<IServiceProvider, DbContextOptionsBuilder> configure)
    {
        if (configureDbContext is not null)
        {
            throw new InvalidOperationException("An EventLoom database provider has already been configured.");
        }

        configureDbContext = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    internal void RegisterServices()
    {
        if (servicesRegistered)
        {
            return;
        }

        servicesRegistered = true;
        Action<ModelBuilder> configureProjectionModel = modelBuilder =>
        {
            foreach (var configure in projectionModelConfigurations)
            {
                configure(modelBuilder);
            }
        };

        services.AddSingleton(registry);
        services.AddSingleton(_ =>
        {
            return new EventSerializer(registry, SerializationOptions, upcasters);
        });
        services.AddSingleton<IEventIdGenerator, UuidV7EventIdGenerator>();
        singleTenantAccessorDescriptor = ServiceDescriptor.Scoped<ITenantAccessor>(_ =>
        {
            if (eventStoreOptions.TenancyMode == TenancyMode.MultiTenant)
            {
                throw MissingMultiTenantAccessor();
            }

            return new SingleTenantAccessor(new TenantId(eventStoreOptions.SingleTenantId));
        });
        services.TryAdd(singleTenantAccessorDescriptor);
        services.AddSingleton(_ => timeProvider);
        services.AddSingleton(_ =>
        {
            ValidateConfiguration();
            return eventStoreOptions;
        });
        services.AddSingleton(workerOptions);
        services.AddSingleton(outboxOptions);
        services.AddSingleton(_ => snapshotRetentionPolicy);
        services.AddSingleton(configureProjectionModel);
        services.AddSingleton(_ => new ProjectionRegistry(projectionRegistrations));
        services.AddDbContext<EventStoreDbContext>((serviceProvider, options) =>
        {
            var configure = configureDbContext
                            ?? throw new InvalidOperationException(
                                "Configure an EventLoom database provider with a provider-specific UsePostgreSql or UseSqlite extension.");
            configure(serviceProvider, options);
        });
        services.AddScoped(serviceProvider => new EventStoreDbContext(
            serviceProvider.GetRequiredService<DbContextOptions<EventStoreDbContext>>(),
            serviceProvider.GetRequiredService<EventStoreOptions>(),
            serviceProvider.GetRequiredService<Action<ModelBuilder>>()));
        services.AddScoped(serviceProvider => new EventStore(
            serviceProvider.GetRequiredService<EventStoreDbContext>(),
            serviceProvider.GetRequiredService<EventSerializer>(),
            serviceProvider.GetRequiredService<IEventIdGenerator>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<EventStoreOptions>(),
            serviceProvider.GetService<ITenantAccessor>(),
            serviceProvider.GetService<IEventStoreRetryPolicy>(),
            serviceProvider.GetService<IInlineProjectionDispatcher>()));
        services.AddScoped<SnapshotStore>();
        services.AddScoped<ProjectionStore>();
        services.AddScoped(serviceProvider => new ProjectionAdministration(
            serviceProvider.GetRequiredService<ProjectionStore>()));
        services.AddScoped<WorkerLeaseStore>();
        services.AddScoped<OutboxStore>();
        services.AddScoped(serviceProvider => new OutboxAdministration(
            serviceProvider.GetRequiredService<OutboxStore>()));
        services.AddScoped(serviceProvider => new EventLoomOperationalDiagnostics(
            serviceProvider.GetRequiredService<ProjectionStore>(),
            serviceProvider.GetRequiredService<OutboxStore>(),
            serviceProvider.GetRequiredService<ProjectionRegistry>()));
    }

    internal void ValidateConfiguration()
    {
        if (configureDbContext is null)
        {
            throw new InvalidOperationException(
                "Configure an EventLoom database provider with a provider-specific UsePostgreSql or UseSqlite extension.");
        }

        new EventSerializer(registry, SerializationOptions).ValidateRegisteredEvents();
        eventStoreOptions.OutboxEnabled = outboxPublisherRegistered;
        eventStoreOptions.SingleTenantId = new TenantId(eventStoreOptions.SingleTenantId).Value;
        outboxOptions.Validate();
        _ = new ProjectionRegistry(projectionRegistrations);
        if (eventStoreOptions.TenancyMode == TenancyMode.MultiTenant &&
            !services.Any(descriptor =>
                descriptor.ServiceType == typeof(ITenantAccessor) &&
                !ReferenceEquals(descriptor, singleTenantAccessorDescriptor)))
        {
            throw MissingMultiTenantAccessor();
        }
    }

    private static InvalidOperationException MissingMultiTenantAccessor() =>
        new(
            "Multi-tenant EventLoom configuration requires a scoped ITenantAccessor. " +
            "Use UseMultiTenancy<TAccessor>() or register ITenantAccessor before AddEventLoom.");

    private static ITenantAccessor ResolveTenantAccessor(IServiceProvider serviceProvider)
    {
        var options = serviceProvider.GetRequiredService<EventStoreOptions>();
        return options.TenancyMode == TenancyMode.SingleTenant
            ? new SingleTenantAccessor(new TenantId(options.SingleTenantId))
            : serviceProvider.GetRequiredService<ITenantAccessor>();
    }

    internal sealed class SingleTenantAccessor(TenantId tenantId) : ITenantAccessor
    {
        public TenantId? TenantId { get; } = tenantId;
    }
}
