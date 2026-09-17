namespace EventLoom;

/// <summary>
/// Handles a typed event as part of an asynchronous, at-least-once projection.
/// </summary>
/// <typeparam name="TEvent">The domain event handled by the projection.</typeparam>
public interface IProjectionHandler<TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>Handles a persisted event envelope.</summary>
    /// <param name="envelope">The typed event and its immutable persistence metadata.</param>
    /// <param name="cancellationToken">Cancels projection execution.</param>
    Task HandleAsync(EventEnvelope<TEvent> envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Handles a typed event inline with its append transaction.
/// </summary>
/// <remarks>
/// Inline handlers must only perform transactional database work. They must not
/// make network calls or depend on effects that cannot be rolled back.
/// </remarks>
/// <typeparam name="TEvent">The domain event handled by the projection.</typeparam>
public interface IInlineProjectionHandler<TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>Handles a persisted event envelope before its append transaction commits.</summary>
    /// <param name="envelope">The typed event and its immutable persistence metadata.</param>
    /// <param name="cancellationToken">Cancels projection execution.</param>
    Task HandleAsync(EventEnvelope<TEvent> envelope, CancellationToken cancellationToken);
}

/// <summary>Dispatches explicitly registered inline projections during an event append.</summary>
public interface IInlineProjectionDispatcher
{
    /// <summary>Dispatches an event to its inline projection handlers.</summary>
    /// <param name="envelope">The persisted event envelope.</param>
    /// <param name="cancellationToken">Cancels projection execution.</param>
    Task DispatchAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}
