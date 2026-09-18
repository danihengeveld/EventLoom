using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EventLoom.Testing;

/// <summary>
/// Hosts a fully wired EventLoom dependency-injection container backed by an in-memory
/// SQLite database for aggregate, event-store, projection, and outbox integration tests.
/// </summary>
/// <remarks>
/// <para>
/// The host keeps one SQLite connection open for its entire lifetime because an
/// in-memory SQLite database is destroyed when its last connection closes. Resolve
/// scoped services such as <c>EventStore</c> and aggregate repositories through
/// <see cref="CreateScope"/> or <c>RunScopedAsync</c>
/// rather than caching them across scopes.
/// </para>
/// <para>
/// This host targets single-node SQLite behavior. It cannot exercise PostgreSQL-specific
/// distributed correctness such as concurrent multi-instance appends or worker lease
/// fencing under contention; use PostgreSQL Testcontainers for that behavior, as the
/// <c>EventLoom.EntityFrameworkCore.PostgreSql.IntegrationTests</c> project does.
/// </para>
/// </remarks>
public sealed class EventLoomSqliteTestHost : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider provider;
    private bool disposed;

    private EventLoomSqliteTestHost(
        SqliteConnection connection,
        ServiceProvider provider,
        ManualTimeProvider clock)
    {
        this.connection = connection;
        this.provider = provider;
        Clock = clock;
    }

    /// <summary>Gets the root service provider for the hosted EventLoom container.</summary>
    /// <remarks>
    /// Prefer <see cref="CreateScope"/> or <c>RunScopedAsync</c>
    /// to resolve scoped services such as <c>EventStore</c> and aggregate repositories.
    /// </remarks>
    public IServiceProvider Services => provider;

    /// <summary>Gets the deterministic clock driving the host's <c>TimeProvider</c>.</summary>
    public ManualTimeProvider Clock { get; }

    /// <summary>
    /// Creates and initializes a SQLite-backed EventLoom test host: opens the kept-open
    /// connection, builds the dependency-injection container, and creates the event-store
    /// schema.
    /// </summary>
    /// <param name="configure">Configures tenancy, determinism, and EventLoom registrations.</param>
    /// <param name="cancellationToken">A token used to cancel schema creation.</param>
    /// <returns>The initialized test host.</returns>
    public static async Task<EventLoomSqliteTestHost> CreateAsync(
        Action<EventLoomTestHostOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var options = new EventLoomTestHostOptions();
        configure?.Invoke(options);

        var connection = new SqliteConnection("Data Source=:memory:");
        ServiceProvider? provider = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var clock = new ManualTimeProvider(options.StartTime);
            var services = new ServiceCollection();
            var builder = services.AddEventLoom();
            options.ConfigureEventLoom?.Invoke(builder);
            builder
                .UseSqlite(_ => connection)
                .UseSingleTenancy(options.TenantId)
                .UseTimeProvider(clock);

            if (options.UseDeterministicEventIds)
            {
                services.AddSingleton<IEventIdGenerator>(new SequentialEventIdGenerator());
            }

            provider = services.BuildServiceProvider(validateScopes: true);

            await using (var scope = provider.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<EventStoreDbContext>();
                await SqliteEventStoreSchema.EnsureCreatedAsync(context, cancellationToken).ConfigureAwait(false);
            }

            return new EventLoomSqliteTestHost(connection, provider, clock);
        }
        catch
        {
            if (provider is not null)
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates a dependency-injection scope for resolving scoped EventLoom services.</summary>
    /// <returns>A disposable scope.</returns>
    public AsyncServiceScope CreateScope() => provider.CreateAsyncScope();

    /// <summary>Runs an action within a new dependency-injection scope.</summary>
    /// <param name="action">Invoked with the scope's service provider.</param>
    /// <param name="cancellationToken">A token passed through to the action.</param>
    public async Task RunScopedAsync(
        Func<IServiceProvider, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = CreateScope();
        await action(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a function within a new dependency-injection scope and returns its result.</summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="action">Invoked with the scope's service provider.</param>
    /// <param name="cancellationToken">A token passed through to the action.</param>
    /// <returns>The result produced by <paramref name="action"/>.</returns>
    public async Task<TResult> RunScopedWithResultAsync<TResult>(
        Func<IServiceProvider, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var scope = CreateScope();
        return await action(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Disposes the dependency-injection container and closes the kept-open connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await provider.DisposeAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
