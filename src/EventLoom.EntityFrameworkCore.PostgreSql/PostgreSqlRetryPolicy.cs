using EventLoom.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

/// <summary>Executes PostgreSQL operations with bounded retries for transient failures.</summary>
internal sealed class PostgreSqlRetryPolicy(
    EventStoreWorkerOptions options, TimeProvider timeProvider, ILogger<PostgreSqlRetryPolicy>? logger = null)
    : IEventStoreRetryPolicy
{
    private readonly EventStoreWorkerOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<PostgreSqlRetryPolicy> logger = logger ?? NullLogger<PostgreSqlRetryPolicy>.Instance;

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
                !cancellationToken.IsCancellationRequested &&
                PostgreSqlExceptionClassifier.IsTransient(PostgreSqlExceptionClassifier.Classify(exception)))
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(1000, 50 * Math.Pow(2, attempt)));
                logger.RetryScheduled(
                    PostgreSqlExceptionClassifier.Classify(exception), attempt + 1, delay.TotalMilliseconds);
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
        }
    }
}
