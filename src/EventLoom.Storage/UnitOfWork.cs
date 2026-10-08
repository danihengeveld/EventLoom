namespace EventLoom.Storage;

/// <summary>
/// Coordinates EventLoom appends with application state under one shared provider transaction.
/// </summary>
/// <remarks>
/// Create one with <see cref="EventStore.BeginUnitOfWorkAsync"/>. Join application state through the storage
/// provider's extension methods, which use <see cref="Transaction"/>. Call <see cref="CommitAsync"/> or
/// <see cref="RollbackAsync"/> exactly once, then dispose the unit of work. Disposing without committing rolls
/// back.
/// </remarks>
public sealed class EventLoomUnitOfWork : IAsyncDisposable
{
    private readonly EventStore store;
    private bool completed;

    internal EventLoomUnitOfWork(EventStore store, IStorageTransaction transaction)
    {
        this.store = store;
        Transaction = transaction;
    }

    /// <summary>Gets the provider transaction shared by this unit of work. Provider extensions downcast it.</summary>
    public IStorageTransaction Transaction { get; }

    /// <summary>Appends a batch atomically within this unit of work's transaction.</summary>
    /// <param name="request">The atomic append to persist.</param>
    /// <param name="cancellationToken">Cancels the append operation.</param>
    /// <returns>The persisted event envelopes.</returns>
    /// <exception cref="InvalidOperationException">
    /// This unit of work has already been committed, rolled back, or disposed.
    /// </exception>
    public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return store.AppendWithinTransactionAsync(request, Transaction, cancellationToken);
    }

    /// <summary>Commits the shared transaction.</summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <exception cref="InvalidOperationException">
    /// This unit of work has already been committed, rolled back, or disposed.
    /// </exception>
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        completed = true;
        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rolls back the shared transaction.</summary>
    /// <param name="cancellationToken">Cancels the rollback.</param>
    /// <exception cref="InvalidOperationException">
    /// This unit of work has already been committed, rolled back, or disposed.
    /// </exception>
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        completed = true;
        await Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rolls back the transaction if it was neither committed nor rolled back, then releases it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!completed)
        {
            completed = true;
            try
            {
                await Transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort rollback during disposal; the provider may already have ended the transaction.
            }
        }

        await Transaction.DisposeAsync().ConfigureAwait(false);
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
