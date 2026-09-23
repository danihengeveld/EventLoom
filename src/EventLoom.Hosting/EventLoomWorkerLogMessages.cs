using Microsoft.Extensions.Logging;

namespace EventLoom.Hosting;

internal static partial class EventLoomWorkerLogMessages
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Debug,
        Message = "Projection worker lost its lease before finishing a batch.")]
    internal static partial void ProjectionLeaseLost(this ILogger logger);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug,
        Message = "Projection {ProjectionName} v{ProjectionVersion} delivery failed on attempt {Attempt} with {ExceptionType}; retrying.")]
    internal static partial void ProjectionRetry(
        this ILogger logger, string projectionName, int projectionVersion, int attempt, string exceptionType);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning,
        Message = "Projection {ProjectionName} v{ProjectionVersion} paused after {Attempts} failed delivery attempts with {ExceptionType}.")]
    internal static partial void ProjectionPaused(
        this ILogger logger, string projectionName, int projectionVersion, int attempts, string exceptionType);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug,
        Message = "Outbox publisher lost its lease before finishing a batch.")]
    internal static partial void OutboxLeaseLost(this ILogger logger);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug,
        Message = "Outbox delivery failed on attempt {Attempt} with {ExceptionType}; retrying.")]
    internal static partial void OutboxRetry(this ILogger logger, int attempt, string exceptionType);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Warning,
        Message = "Outbox delivery exhausted {Attempts} attempts with {ExceptionType}; message remains pending.")]
    internal static partial void OutboxAttemptsExhausted(this ILogger logger, int attempts, string exceptionType);
}
