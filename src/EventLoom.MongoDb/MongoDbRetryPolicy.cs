using EventLoom.Hosting;
using EventLoom.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

internal sealed class MongoDbRetryPolicy(
    EventStoreWorkerOptions options,
    TimeProvider timeProvider,
    ILogger<MongoDbRetryPolicy>? logger = null) : IEventStoreRetryPolicy
{
    private readonly EventStoreWorkerOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<MongoDbRetryPolicy> logger = logger ?? NullLogger<MongoDbRetryPolicy>.Instance;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < options.MaxRetryAttempts &&
                !cancellationToken.IsCancellationRequested &&
                MongoDbExceptionClassifier.IsRetryable(MongoDbExceptionClassifier.Classify(exception)))
            {
                var classification = MongoDbExceptionClassifier.Classify(exception);
                var delay = TimeSpan.FromMilliseconds(Math.Min(1000, 50 * Math.Pow(2, attempt)));
                logger.LogDebug(
                    "Retrying EventLoom MongoDB append after {Classification} on attempt {Attempt} with delay {Delay}ms.",
                    classification,
                    attempt + 1,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal enum MongoDbExceptionClassification
{
    Unknown,
    ConnectionFailure,
    TransientTransaction,
    UnknownCommitResult,
    WriteConflict,
    NoSuchTransaction,
    DuplicateKey
}

internal static class MongoDbExceptionClassifier
{
    public static MongoDbExceptionClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var providerException = exception;
        while (providerException is EventStoreConcurrencyException && providerException.InnerException is not null)
        {
            providerException = providerException.InnerException;
        }

        if (providerException is MongoConnectionException)
        {
            return MongoDbExceptionClassification.ConnectionFailure;
        }

        if (providerException is MongoException mongoException)
        {
            if (mongoException.HasErrorLabel("UnknownTransactionCommitResult"))
            {
                return MongoDbExceptionClassification.UnknownCommitResult;
            }

            if (mongoException.HasErrorLabel("TransientTransactionError"))
            {
                return MongoDbExceptionClassification.TransientTransaction;
            }

            return mongoException switch
            {
                MongoWriteException { WriteError.Code: 11000 or 11001 or 12582 } => MongoDbExceptionClassification
                    .DuplicateKey,
                MongoBulkWriteException<EventDocument> { WriteErrors: var errors } when errors.Any(error =>
                        error.Code is 11000 or 11001 or 12582)
                    => MongoDbExceptionClassification.DuplicateKey,
                MongoCommandException { Code: 11000 or 11001 or 12582 } => MongoDbExceptionClassification.DuplicateKey,
                MongoWriteException { WriteError.Code: 112 } => MongoDbExceptionClassification.WriteConflict,
                MongoCommandException { Code: 112 } => MongoDbExceptionClassification.WriteConflict,
                MongoCommandException { Code: 251 } => MongoDbExceptionClassification.NoSuchTransaction,
                _ => MongoDbExceptionClassification.Unknown
            };
        }

        return MongoDbExceptionClassification.Unknown;
    }

    public static bool IsRetryable(MongoDbExceptionClassification classification) =>
        classification is MongoDbExceptionClassification.ConnectionFailure
            or MongoDbExceptionClassification.TransientTransaction
            or MongoDbExceptionClassification.UnknownCommitResult
            or MongoDbExceptionClassification.WriteConflict
            or MongoDbExceptionClassification.NoSuchTransaction;

    public static bool IsConcurrency(MongoDbExceptionClassification classification) =>
        classification is MongoDbExceptionClassification.TransientTransaction
            or MongoDbExceptionClassification.WriteConflict
            or MongoDbExceptionClassification.NoSuchTransaction
            or MongoDbExceptionClassification.DuplicateKey;
}
