using EventLoom.Hosting;
using EventLoom.Storage;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

/// <summary>The MongoDB transaction scope a transactional projection writes its read model in.</summary>
public sealed class MongoProjectionTransaction : IProjectionTransactionContext
{
    internal MongoProjectionTransaction(IClientSessionHandle session, IMongoDatabase database)
    {
        Session = session;
        Database = database;
    }

    /// <summary>Gets the MongoDB client session participating in the checkpoint transaction.</summary>
    public IClientSessionHandle Session { get; }

    /// <summary>Gets the MongoDB database participating in the checkpoint transaction.</summary>
    public IMongoDatabase Database { get; }
}

/// <summary>Handles a typed event inside the EventLoom MongoDB projection transaction.</summary>
/// <typeparam name="TEvent">The domain event handled by the projection.</typeparam>
public interface IMongoProjectionHandler<TEvent>
{
    /// <summary>Updates the read model for the typed event.</summary>
    /// <param name="envelope">The typed event and immutable persistence metadata.</param>
    /// <param name="transaction">The MongoDB session and database participating in the checkpoint transaction.</param>
    /// <param name="cancellationToken">Cancels projection execution.</param>
    Task HandleAsync(
        EventEnvelope<TEvent> envelope,
        MongoProjectionTransaction transaction,
        CancellationToken cancellationToken);
}

/// <summary>Registers MongoDB transactional projections.</summary>
public static class MongoProjectionRegistrationExtensions
{
    /// <param name="builder">The projection registration builder.</param>
    extension(ProjectionRegistrationBuilder builder)
    {
        /// <summary>
        /// Registers a typed handler whose read-model changes and checkpoint commit in one MongoDB transaction.
        /// </summary>
        /// <typeparam name="THandler">The projection handler type.</typeparam>
        /// <typeparam name="TEvent">The event type handled by the projection.</typeparam>
        /// <returns>This projection registration builder.</returns>
        public ProjectionRegistrationBuilder Transactional<THandler, TEvent>()
            where THandler : class, IMongoProjectionHandler<TEvent>
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.RegisterTransactional<THandler, TEvent>(
                static (handler, envelope, context, cancellationToken) =>
                    handler.HandleAsync(
                        envelope,
                        (MongoProjectionTransaction)context,
                        cancellationToken));
        }
    }
}
