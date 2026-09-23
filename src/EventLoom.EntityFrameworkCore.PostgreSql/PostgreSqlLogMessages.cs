using Microsoft.Extensions.Logging;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

internal static partial class PostgreSqlLogMessages
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug,
        Message = "Retrying PostgreSQL event-store operation after {Classification} (attempt {Attempt}, delay {DelayMilliseconds} ms).")]
    internal static partial void RetryScheduled(
        this ILogger logger, PostgreSqlExceptionClassification classification, int attempt, double delayMilliseconds);
}
