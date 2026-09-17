namespace EventLoom.EntityFrameworkCore;

internal interface IEntityWithId
{
    long Id { get; set; }
}

internal sealed class StreamEntity
{
    public required string TenantId { get; set; }
    public required string StreamId { get; set; }
    public required string AggregateType { get; set; }
    public long Version { get; set; }
}

internal sealed class EventEntity
{
    public Guid EventId { get; set; }
    public required string TenantId { get; set; }
    public required string StreamId { get; set; }
    public required string AggregateType { get; set; }
    public long StreamVersion { get; set; }
    public long TenantOffset { get; set; }
    public required string EventType { get; set; }
    public int EventTypeVersion { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public string? Actor { get; set; }
    public string? Headers { get; set; }
    public string? AppendId { get; set; }
}

internal sealed class TenantOffsetEntity
{
    public string? TenantId { get; set; }
    public long NextOffset { get; set; }
}

internal sealed class SnapshotEntity : IEntityWithId
{
    public long Id { get; set; }
    public required string TenantId { get; set; }
    public required string StreamId { get; set; }
    public required string AggregateType { get; set; }
    public long StreamVersion { get; set; }
    public required string SnapshotType { get; set; }
    public int SchemaVersion { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ProjectionCheckpointEntity
{
    public required string TenantId { get; set; }
    public required string ProjectionName { get; set; }
    public int ProjectionVersion { get; set; }
    public long TenantOffset { get; set; }
    public ProjectionStatus Status { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ProjectionLeaseEntity
{
    public required string TenantId { get; set; }
    public required string LeaseName { get; set; }
    public required string OwnerId { get; set; }
    public long FencingToken { get; set; }
    public DateTimeOffset LeaseUntil { get; set; }
}

internal sealed class ProjectionFailureEntity : IEntityWithId
{
    public long Id { get; set; }
    public required string TenantId { get; set; }
    public required string ProjectionName { get; set; }
    public int ProjectionVersion { get; set; }
    public Guid EventId { get; set; }
    public required string EventType { get; set; }
    public long TenantOffset { get; set; }
    public int AttemptCount { get; set; }
    public required string ExceptionType { get; set; }
    public DateTimeOffset FailedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public bool WasSkipped { get; set; }
}

internal sealed class OutboxEntity : IEntityWithId
{
    public long Id { get; set; }
}
