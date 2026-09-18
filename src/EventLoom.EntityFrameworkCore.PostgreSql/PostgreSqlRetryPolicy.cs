using EventLoom.Hosting;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

/// <summary>Executes PostgreSQL operations with bounded retries for transient failures.</summary>
public sealed class PostgreSqlRetryPolicy(EventStoreWorkerOptions options, TimeProvider timeProvider)
    : IEventStoreRetryPolicy
{
    private readonly EventStoreWorkerOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Executes an operation, retrying only classified transient failures.</summary>
    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (Exception exception) when (
                attempt < options.MaxRetryAttempts &&
                PostgreSqlExceptionClassifier.IsTransient(PostgreSqlExceptionClassifier.Classify(exception)))
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(1000, 50 * Math.Pow(2, attempt)));
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
    }
}
