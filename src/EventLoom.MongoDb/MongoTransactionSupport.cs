using EventLoom.Storage;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

/// <summary>The MongoDB client session an EventLoom unit of work shares with application code.</summary>
public sealed class MongoStorageTransaction : IStorageTransaction
{
    private readonly MongoStorageCatalog catalog;
    private bool completed;

    internal MongoStorageTransaction(MongoStorageCatalog catalog, IClientSessionHandle session)
    {
        this.catalog = catalog;
        Session = session;
    }

    /// <summary>Gets the MongoDB client session that owns the EventLoom transaction.</summary>
    public IClientSessionHandle Session { get; }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        completed = true;
        await MongoTransactionUtilities.CommitWithRetryAsync(Session, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        completed = true;
        await MongoTransactionUtilities.AbortIfNeededAsync(Session, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!completed)
        {
            completed = true;
            await MongoTransactionUtilities.AbortIfNeededAsync(Session, CancellationToken.None).ConfigureAwait(false);
        }

        Session.Dispose();
        GC.SuppressFinalize(this);
    }

    internal MongoStorageCatalog Catalog => catalog;
}

/// <summary>Provides MongoDB-specific access to an EventLoom unit of work.</summary>
public static class EventLoomUnitOfWorkMongoDbExtensions
{
    /// <param name="unitOfWork">The EventLoom unit of work to inspect.</param>
    extension(EventLoomUnitOfWork unitOfWork)
    {
        /// <summary>
        /// Gets the MongoDB client session shared by this unit of work.
        /// </summary>
        /// <exception cref="InvalidOperationException">The unit of work was not created by the MongoDB provider.</exception>
        public IClientSessionHandle Session => GetTransaction(unitOfWork).Session;
    }

    private static MongoStorageTransaction GetTransaction(EventLoomUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        return unitOfWork.Transaction as MongoStorageTransaction
               ?? throw new InvalidOperationException(
                   "This unit of work was not created by the EventLoom MongoDB storage provider.");
    }
}

internal static class MongoTransactionUtilities
{
    public static async Task CommitWithRetryAsync(
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await session.CommitTransactionAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                MongoDbExceptionClassifier.Classify(exception) == MongoDbExceptionClassification.UnknownCommitResult &&
                !cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    public static async Task AbortIfNeededAsync(
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
        {
            return;
        }

        try
        {
            await session.AbortTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort only; the server may already have ended the transaction.
        }
    }
}
