using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Coordinates EventLoom appends with application database operations under one shared
/// relational transaction, without callers directly managing a <see cref="DbConnection"/> or
/// <see cref="DbTransaction"/>.
/// </summary>
/// <remarks>
/// Create one with <see cref="EventStore.BeginUnitOfWorkAsync"/>. Enlist application
/// <see cref="DbContext"/> instances with <see cref="EnlistAsync"/>; each must be configured
/// with the exact same scoped database connection as the EventLoom event store context. Call
/// <see cref="CommitAsync"/> or <see cref="RollbackAsync"/> exactly once, then dispose the unit
/// of work. Disposing without committing rolls back. The advanced
/// <see cref="EventStore.AppendInTransactionAsync"/> path remains available for callers that
/// already manage a <see cref="DbTransaction"/> directly.
/// </remarks>
public sealed class EventLoomUnitOfWork : IAsyncDisposable
{
    private readonly EventStore store;
    private readonly EventStoreDbContext context;
    private readonly IDbContextTransaction transaction;
    private bool completed;

    internal EventLoomUnitOfWork(EventStore store, EventStoreDbContext context, IDbContextTransaction transaction)
    {
        this.store = store;
        this.context = context;
        this.transaction = transaction;
    }

    /// <summary>
    /// The underlying relational transaction, for interop with code that requires the raw
    /// ADO.NET type.
    /// </summary>
    public DbTransaction DbTransaction => transaction.GetDbTransaction();

    /// <summary>
    /// Enlists an application <see cref="DbContext"/> into this unit of work's transaction.
    /// </summary>
    /// <param name="applicationContext">
    /// The application context to enlist. It must be configured with the exact same scoped
    /// database connection as the EventLoom event store context.
    /// </param>
    /// <param name="cancellationToken">Cancels the enlistment.</param>
    /// <exception cref="ArgumentNullException"><paramref name="applicationContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// This unit of work has already been committed, rolled back, or disposed, or
    /// <paramref name="applicationContext"/> does not share the event store's connection.
    /// </exception>
    public async Task EnlistAsync(DbContext applicationContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationContext);
        EnsureActive();
        if (!ReferenceEquals(applicationContext.Database.GetDbConnection(), context.Database.GetDbConnection()))
        {
            throw new InvalidOperationException(
                "The application DbContext must use the exact same scoped DbConnection instance as the " +
                "EventLoom event store context. Register both contexts with the same scoped DbConnection to " +
                "share a unit of work.");
        }

        await applicationContext.Database.UseTransactionAsync(DbTransaction, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a batch atomically within this unit of work's transaction.
    /// </summary>
    /// <param name="request">The atomic append to persist.</param>
    /// <param name="cancellationToken">Cancels the append operation.</param>
    /// <returns>The persisted event envelopes.</returns>
    /// <exception cref="InvalidOperationException">
    /// This unit of work has already been committed, rolled back, or disposed.
    /// </exception>
    public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return store.AppendWithinAmbientTransactionAsync(request, cancellationToken);
    }

    /// <summary>
    /// Commits every enlisted context's transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <exception cref="InvalidOperationException">
    /// This unit of work has already been committed, rolled back, or disposed.
    /// </exception>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        completed = true;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back every enlisted context's transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancels the rollback.</param>
    /// <exception cref="InvalidOperationException">
    /// This unit of work has already been committed, rolled back, or disposed.
    /// </exception>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        completed = true;
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rolls back the transaction if it was neither committed nor rolled back, then releases it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!completed)
        {
            completed = true;
            try
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort rollback during disposal; the connection or provider may already
                // have terminated the transaction.
            }
        }

        await transaction.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureActive()
    {
        if (completed)
        {
            throw new InvalidOperationException(
                "This unit of work has already been committed, rolled back, or disposed.");
        }
    }
}
