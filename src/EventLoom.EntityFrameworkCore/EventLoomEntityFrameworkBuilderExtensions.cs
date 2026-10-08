using EventLoom.Hosting;
using EventLoom.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Configures the EF Core storage provider. Relational providers such as PostgreSQL and SQLite register
/// themselves through their own <c>Use*</c> extensions and then accept this shared configuration.
/// </summary>
public static class EventLoomEntityFrameworkBuilderExtensions
{
    /// <param name="builder">The EventLoom builder to configure.</param>
    extension(EventLoomBuilder builder)
    {
        /// <summary>Configures the names and schema used by the EventLoom tables.</summary>
        /// <param name="configure">Configures the EF Core storage options.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        public EventLoomBuilder ConfigureEntityFramework(Action<EntityFrameworkStorageOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            configure(GetState(builder).Options);
            return builder;
        }

        /// <summary>Adds read-model mappings to the EventLoom context used by transactional projections.</summary>
        /// <param name="configure">Configures one or more EF Core read-model entity mappings.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        public EventLoomBuilder ConfigureProjectionModel(Action<ModelBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            GetState(builder).ModelConfigurations.Add(configure);
            return builder;
        }

        /// <summary>Configures the EF Core options of the EventLoom context.</summary>
        /// <param name="configure">Applies the database provider configuration.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The context was already configured.</exception>
        public EventLoomBuilder ConfigureDbContext(Action<DbContextOptionsBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return ConfigureDbContext(builder, (_, options) => configure(options));
        }

        /// <summary>Configures the EF Core options of the EventLoom context using application services.</summary>
        /// <param name="configure">Applies the database provider configuration.</param>
        /// <returns>This builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The context was already configured.</exception>
        public EventLoomBuilder ConfigureDbContext(Action<IServiceProvider, DbContextOptionsBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            var state = GetState(builder);
            if (state.ConfigureDbContext is not null)
            {
                throw new InvalidOperationException("The EventLoom database context has already been configured.");
            }

            state.ConfigureDbContext = configure;
            return builder;
        }
    }

    /// <summary>
    /// Registers EF Core as the EventLoom storage provider. Called by relational provider packages.
    /// </summary>
    /// <param name="builder">The EventLoom builder to configure.</param>
    /// <param name="providerName">The relational provider name reported in diagnostics.</param>
    /// <param name="isDistributed">Whether the database supports multi-process deployments.</param>
    /// <param name="useSchema">Whether the database supports named schemas.</param>
    /// <param name="dialect">The provider-specific append and lease strategy.</param>
    /// <param name="configureDbContext">Applies the database provider configuration.</param>
    /// <returns>The configured builder.</returns>
    internal static EventLoomBuilder UseEntityFramework(
        EventLoomBuilder builder,
        string providerName,
        bool isDistributed,
        bool useSchema,
        EfStorageDialect dialect,
        Action<IServiceProvider, DbContextOptionsBuilder> configureDbContext)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(configureDbContext);
        var state = GetState(builder);
        state.Options.UseSchema = useSchema;
        builder.ConfigureDbContext(configureDbContext);
        return builder.UseStorage(
            new StorageCapabilities(
                providerName,
                SupportsTransactionalProjections: true,
                SupportsInlineProjections: true,
                SupportsUnitOfWork: true,
                IsDistributed: isDistributed),
            services =>
            {
                services.AddSingleton(state.Options);
                services.AddSingleton(dialect);
                services.AddSingleton<Action<ModelBuilder>>(modelBuilder =>
                {
                    foreach (var configure in state.ModelConfigurations)
                    {
                        configure(modelBuilder);
                    }
                });
                services.AddDbContext<EventStoreDbContext>((serviceProvider, options) =>
                    state.ConfigureDbContext!(serviceProvider, options));
                services.AddScoped(serviceProvider => new EventStoreDbContext(
                    serviceProvider.GetRequiredService<DbContextOptions<EventStoreDbContext>>(),
                    serviceProvider.GetRequiredService<EntityFrameworkStorageOptions>(),
                    serviceProvider.GetRequiredService<Action<ModelBuilder>>()));
                services.AddScoped<IEventStorage, EfEventStorage>();
                services.AddScoped<ISnapshotStorage, EfSnapshotStorage>();
                services.AddScoped<IProjectionStorage, EfProjectionStorage>();
                services.AddScoped<IOutboxStorage, EfOutboxStorage>();
                services.AddScoped<IWorkerLeaseStorage, EfWorkerLeaseStorage>();
                services.AddScoped<IStorageSchema, EfStorageSchema>();
            });
    }

    private static EfBuilderState GetState(EventLoomBuilder builder)
    {
        var descriptor = builder.Services.FirstOrDefault(value => value.ServiceType == typeof(EfBuilderState));
        if (descriptor?.ImplementationInstance is EfBuilderState existing)
        {
            return existing;
        }

        var state = new EfBuilderState();
        builder.Services.AddSingleton(state);
        return state;
    }

    private sealed class EfBuilderState
    {
        public EntityFrameworkStorageOptions Options { get; } = new();

        public List<Action<ModelBuilder>> ModelConfigurations { get; } = [];

        public Action<IServiceProvider, DbContextOptionsBuilder>? ConfigureDbContext { get; set; }
    }
}
