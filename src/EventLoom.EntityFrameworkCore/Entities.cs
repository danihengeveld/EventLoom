namespace EventLoom.EntityFrameworkCore;

internal sealed class StreamEntity
{
    public string? TenantId { get; set; }
    public required string StreamId { get; set; }
    public required string AggregateType { get; set; }
    public long Version { get; set; }
}

internal sealed class EventEntity
{
    public Guid EventId { get; set; }
    public string? TenantId { get; set; }
    public required string StreamId { get; set; }
    public required string AggregateType { get; set; }
    public long StreamVersion { get; set; }
    public long GlobalPosition { get; set; }
    public required string EventType { get; set; }
    public int EventTypeVersion { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class TenantPositionEntity
{
    public string? TenantId { get; set; }
    public long NextPosition { get; set; }
}

internal sealed class SnapshotEntity
{
    public long Id { get; set; }
}

internal sealed class ProjectionCheckpointEntity
{
    public long Id { get; set; }
}

internal sealed class ProjectionLeaseEntity
{
    public long Id { get; set; }
}

internal sealed class ProjectionFailureEntity
{
    public long Id { get; set; }
}

internal sealed class OutboxEntity
{
    public long Id { get; set; }
}
