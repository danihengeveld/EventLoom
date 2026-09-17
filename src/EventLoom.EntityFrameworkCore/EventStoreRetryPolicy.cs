namespace EventLoom.EntityFrameworkCore;

/// <summary>
/// Executes an event-store operation with provider-specific retry behavior.
/// </summary>
public interface IEventStoreRetryPolicy
{
    /// <summary>
    /// Executes an operation according to the configured provider retry policy.
    /// </summary>
    /// <typeparam name="T">The operation result type.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The operation result.</returns>
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}

internal sealed class NoopEventStoreRetryPolicy : IEventStoreRetryPolicy
{
    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        operation(cancellationToken);
}
