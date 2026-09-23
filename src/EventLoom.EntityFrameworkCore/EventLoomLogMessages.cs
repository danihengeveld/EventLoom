using Microsoft.Extensions.Logging;

namespace EventLoom.EntityFrameworkCore;

internal static partial class EventLoomLogMessages
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug,
        Message = "Event append rejected for aggregate {AggregateType} with {ExceptionType}.")]
    internal static partial void AppendRejected(this ILogger logger, string aggregateType, string exceptionType);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "Event append failed for aggregate {AggregateType} with {ExceptionType}.")]
    internal static partial void AppendFailed(this ILogger logger, string aggregateType, string exceptionType);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "Snapshot {SnapshotType} schema v{SchemaVersion} could not be restored ({Reason}); replaying history. Invalidated: {Invalidated}.")]
    internal static partial void SnapshotFallback(
        this ILogger logger, string snapshotType, int schemaVersion, SnapshotInvalidationReason reason, bool invalidated);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information,
        Message = "Projection {ProjectionName} v{ProjectionVersion} resumed.")]
    internal static partial void ProjectionResumed(this ILogger logger, string projectionName, int projectionVersion);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Projection {ProjectionName} v{ProjectionVersion} skipped a failed event and resumed.")]
    internal static partial void ProjectionSkipped(this ILogger logger, string projectionName, int projectionVersion);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Information,
        Message = "Projection {ProjectionName} v{ProjectionVersion} checkpoint reset for replay.")]
    internal static partial void ProjectionReplayStarted(this ILogger logger, string projectionName, int projectionVersion);
}
