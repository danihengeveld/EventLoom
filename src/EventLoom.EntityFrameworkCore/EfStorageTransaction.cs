using System.Data.Common;
using EventLoom.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EventLoom.EntityFrameworkCore;

/// <summary>The relational transaction an EventLoom unit of work shares with application contexts.</summary>
public sealed class EfStorageTransaction : IStorageTransaction
{
    private readonly IDbContextTransaction transaction;

    internal EfStorageTransaction(EventStoreDbContext context, IDbContextTransaction transaction)
    {
        Context = context;
        this.transaction = transaction;
    }

    internal EventStoreDbContext Context { get; }

    /// <summary>Gets the underlying ADO.NET transaction, for interop with code that requires the raw type.</summary>
    public DbTransaction DbTransaction => transaction.GetDbTransaction();

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        transaction.CommitAsync(cancellationToken);

    /// <inheritdoc />
    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        transaction.RollbackAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => transaction.DisposeAsync();
}

/// <summary>Joins application EF Core contexts to an EventLoom unit of work.</summary>
public static class EventLoomUnitOfWorkEntityFrameworkExtensions
{
    /// <param name="unitOfWork">The unit of work to inspect or join.</param>
    extension(EventLoomUnitOfWork unitOfWork)
    {
        /// <summary>
        /// Gets the underlying relational transaction, for interop with code that requires the raw ADO.NET type.
        /// </summary>
        /// <exception cref="InvalidOperationException">The unit of work was not created by the EF Core provider.</exception>
        public DbTransaction DbTransaction => GetTransaction(unitOfWork).DbTransaction;

        /// <summary>
        /// Enlists an application <see cref="DbContext"/> into this unit of work's transaction.
        /// </summary>
        /// <param name="applicationContext">
        /// The application context to enlist. It must be configured with the exact same scoped database
        /// connection as the EventLoom event store context.
        /// </param>
        /// <param name="cancellationToken">Cancels the enlistment.</param>
        /// <exception cref="ArgumentNullException"><paramref name="applicationContext"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The unit of work was not created by the EF Core provider, or <paramref name="applicationContext"/>
        /// does not share the event store's connection.
        /// </exception>
        public async Task EnlistAsync(DbContext applicationContext, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(applicationContext);
            var transaction = GetTransaction(unitOfWork);
            if (!ReferenceEquals(
                    applicationContext.Database.GetDbConnection(),
                    transaction.Context.Database.GetDbConnection()))
            {
                throw new InvalidOperationException(
                    "The application DbContext must use the exact same scoped DbConnection instance as the " +
                    "EventLoom event store context. Register both contexts with the same scoped DbConnection to " +
                    "share a unit of work.");
            }

            await applicationContext.Database
                .UseTransactionAsync(transaction.DbTransaction, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static EfStorageTransaction GetTransaction(EventLoomUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        return unitOfWork.Transaction as EfStorageTransaction
               ?? throw new InvalidOperationException(
                   "This unit of work was not created by the EventLoom EF Core storage provider.");
    }
}
