using EventLoom.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    /// Adds EventLoom's event registry, serializer, event store, and clock.
    /// Events and a storage provider must be explicitly configured through <paramref name="configure"/>.
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
    private readonly EventStoreOptions eventStoreOptions = new();
    private EventStoreWorkerOptions workerOptions = new();
    private readonly OutboxOptions outboxOptions = new();
    private ISnapshotRetentionPolicy snapshotRetentionPolicy = new KeepLatestSnapshotsPolicy(1);
    private TimeProvider timeProvider = TimeProvider.System;
    private StorageCapabilities? storageCapabilities;
    private bool outboxPublisherRegistered;
    private bool servicesRegistered;
    private ServiceDescriptor? singleTenantAccessorDescriptor;

    internal EventLoomBuilder(IServiceCollection services)
    {
        this.services = services;
    }

    /// <summary>
    /// Registers the events owned by an aggregate without registering an aggregate repository.
    /// </summary>
    /// <remarks>
    /// <see cref="AddAggregate{TAggregate, TId}"/> registers owned events automatically. Use this method only when
    /// events are appended and read through <see cref="EventStore"/> without an aggregate repository.
    /// </remarks>
    /// <typeparam name="TAggregate">The aggregate whose owned events are registered.</typeparam>
    /// <returns>This builder.</returns>
    public EventLoomBuilder AddAggregateEvents<TAggregate>()
        where TAggregate : Aggregate
    {
        registry.RegisterAggregate<TAggregate>();
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

    /// <summary>Gets the service collection EventLoom registers into, for use by storage providers.</summary>
    public IServiceCollection Services => services;

    /// <summary>
    /// Registers the storage provider that persists events, snapshots, projections, the outbox, and leases.
    /// </summary>
    /// <remarks>
    /// This is the extension point for storage providers; applications call a provider-specific method such as
    /// <c>UsePostgreSql</c>, <c>UseSqlite</c>, or <c>UseMongoDb</c> instead. The <paramref name="register"/>
    /// callback must register scoped implementations of <see cref="IEventStorage"/>,
    /// <see cref="ISnapshotStorage"/>, <see cref="IProjectionStorage"/>, <see cref="IOutboxStorage"/>,
    /// <see cref="IWorkerLeaseStorage"/>, and <see cref="IStorageSchema"/>.
    /// </remarks>
    /// <param name="capabilities">Describes what the provider guarantees beyond the mandatory contract.</param>
    /// <param name="register">Registers the provider's services.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> or <paramref name="register"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A storage provider has already been configured.</exception>
    public EventLoomBuilder UseStorage(StorageCapabilities capabilities, Action<IServiceCollection> register)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(register);
        if (storageCapabilities is not null)
        {
            throw new InvalidOperationException(
                $"An EventLoom storage provider has already been configured ({storageCapabilities.ProviderName}).");
        }

        storageCapabilities = capabilities;
        services.AddSingleton(capabilities);
        register(services);
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
    /// Registers a typed projection handler whose read-model changes and checkpoint commit in one provider transaction.
    /// </summary>
    internal EventLoomBuilder AddTransactionalProjection<TProjection, TEvent>(
        string name,
        int version,
        Func<TProjection, EventEnvelope<TEvent>, IProjectionTransactionContext, CancellationToken, Task> invoke)
        where TProjection : class
    {
        ArgumentNullException.ThrowIfNull(invoke);
        var key = new ProjectionKey(name, version);
        key.Validate();
        services.TryAddScoped<TProjection>();
        projectionRegistrations.Add(ProjectionHandlerRegistration.CreateTransactional(key, invoke));
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

    /// <summary>
    /// Registers an aggregate repository and every event the aggregate owns and handles through
    /// <see cref="IApply{TEvent}"/>.
    /// </summary>
    /// <typeparam name="TAggregate">The aggregate type.</typeparam>
    /// <typeparam name="TId">The aggregate identifier type.</typeparam>
    /// <param name="configure">Configures aggregate construction, stream identity, and optional snapshots.</param>
    /// <returns>This builder.</returns>
    public EventLoomBuilder AddAggregate<TAggregate, TId>(
        Action<AggregateRegistrationBuilder<TAggregate, TId>> configure)
        where TAggregate : Aggregate<TAggregate, TId>
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AggregateRegistrationBuilder<TAggregate, TId>();
        configure(builder);
        var registration = builder.Build();
        registry.RegisterAggregate<TAggregate>();

        var snapshots = registration.SnapshotConfiguration;
        services.AddScoped(serviceProvider => new AggregateRepository<TAggregate, TId>(
            serviceProvider.GetRequiredService<EventStore>(),
            registration.Factory,
            registration.AggregateType,
            registration.StreamId,
            ResolveTenantAccessor(serviceProvider),
            snapshots is null ? null : serviceProvider.GetRequiredService<SnapshotStore>(),
            snapshots?.Dispatcher,
            snapshots?.Policy,
            snapshots?.Invalidator,
            snapshots?.RetentionPolicy,
            serviceProvider.GetRequiredService<ILogger<AggregateRepository<TAggregate, TId>>>()));
        return this;
    }

    internal void RegisterServices()
    {
        if (servicesRegistered)
        {
            return;
        }

        servicesRegistered = true;
        services.AddSingleton(registry);
        services.AddSingleton(_ => { return new EventSerializer(registry, SerializationOptions, upcasters); });
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
        services.AddSingleton(_ => new ProjectionRegistry(projectionRegistrations));
        services.AddScoped(serviceProvider => new EventStore(
            serviceProvider.GetRequiredService<IEventStorage>(),
            serviceProvider.GetRequiredService<EventSerializer>(),
            serviceProvider.GetRequiredService<IEventIdGenerator>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<EventStoreOptions>(),
            serviceProvider.GetService<ITenantAccessor>(),
            serviceProvider.GetService<IEventStoreRetryPolicy>(),
            serviceProvider.GetService<IInlineProjectionDispatcher>(),
            serviceProvider.GetRequiredService<ILogger<EventStore>>()));
        services.AddScoped(serviceProvider => new SnapshotStore(
            serviceProvider.GetRequiredService<ISnapshotStorage>(),
            serviceProvider.GetRequiredService<TimeProvider>(),
            serviceProvider.GetRequiredService<ISnapshotRetentionPolicy>()));
        services.AddScoped(serviceProvider => new ProjectionStore(
            serviceProvider.GetRequiredService<IProjectionStorage>(),
            serviceProvider.GetRequiredService<IEventStorage>(),
            serviceProvider.GetRequiredService<ILogger<ProjectionStore>>()));
        services.AddScoped(serviceProvider => new ProjectionAdministration(
            serviceProvider.GetRequiredService<ProjectionStore>()));
        services.AddScoped(serviceProvider => new OutboxStore(
            serviceProvider.GetRequiredService<IOutboxStorage>()));
        services.AddScoped(serviceProvider => new OutboxAdministration(
            serviceProvider.GetRequiredService<OutboxStore>()));
        services.AddScoped(serviceProvider => new EventLoomOperationalDiagnostics(
            serviceProvider.GetRequiredService<ProjectionStore>(),
            serviceProvider.GetRequiredService<OutboxStore>(),
            serviceProvider.GetRequiredService<ProjectionRegistry>()));
    }

    internal void ValidateConfiguration()
    {
        if (storageCapabilities is null)
        {
            throw new InvalidOperationException(
                "Configure an EventLoom storage provider with a provider-specific extension such as " +
                "UsePostgreSql, UseSqlite, or UseMongoDb.");
        }

        foreach (var registration in projectionRegistrations)
        {
            if (registration.Mode == ProjectionMode.Transactional && !storageCapabilities.SupportsTransactionalProjections)
            {
                throw new InvalidOperationException(
                    $"The {storageCapabilities.ProviderName} storage provider does not support transactional projections.");
            }

            if (registration.Mode == ProjectionMode.Inline && !storageCapabilities.SupportsInlineProjections)
            {
                throw new InvalidOperationException(
                    $"The {storageCapabilities.ProviderName} storage provider does not support inline projections.");
            }
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
