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

internal sealed class SnapshotEntity : IEntityWithId
{
    public long Id { get; set; }
}

internal sealed class ProjectionCheckpointEntity : IEntityWithId
{
    public long Id { get; set; }
}

internal sealed class ProjectionLeaseEntity : IEntityWithId
{
    public long Id { get; set; }
}

internal sealed class ProjectionFailureEntity : IEntityWithId
{
    public long Id { get; set; }
}

internal sealed class OutboxEntity : IEntityWithId
{
    public long Id { get; set; }
}
