using EventLoom.Hosting;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Handles a typed event in the EventLoom context transaction.</summary>
/// <typeparam name="TEvent">The domain event handled by the projection.</typeparam>
public interface IEfProjectionHandler<TEvent>
{
    /// <summary>Updates the read model for the typed event.</summary>
    /// <param name="envelope">The typed event and immutable persistence metadata.</param>
    /// <param name="context">The EventLoom context participating in the checkpoint transaction.</param>
    /// <param name="cancellationToken">Cancels projection execution.</param>
    Task HandleAsync(
        EventEnvelope<TEvent> envelope,
        EventStoreDbContext context,
        CancellationToken cancellationToken);
}

/// <summary>Registers EF Core transactional projections.</summary>
public static class EfProjectionRegistrationExtensions
{
    /// <param name="builder">The projection registration builder.</param>
    extension(ProjectionRegistrationBuilder builder)
    {
        /// <summary>
        /// Registers a typed handler whose read-model changes and checkpoint commit in one EF Core transaction.
        /// </summary>
        /// <typeparam name="TProjection">The projection handler type.</typeparam>
        /// <typeparam name="TEvent">The event type handled by the projection.</typeparam>
        /// <returns>This projection registration builder.</returns>
        public ProjectionRegistrationBuilder Transactional<TProjection, TEvent>()
            where TProjection : class, IEfProjectionHandler<TEvent>
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.RegisterTransactional<TProjection, TEvent>(
                (handler, envelope, transaction, cancellationToken) =>
                    handler.HandleAsync(
                        envelope,
                        ((EfProjectionTransaction)transaction).Context,
                        cancellationToken));
        }
    }
}
