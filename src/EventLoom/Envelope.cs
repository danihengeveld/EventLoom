using System.Collections.ObjectModel;

namespace EventLoom;

public sealed record EventMetadata(
    string? CorrelationId = null,
    string? CausationId = null,
    string? Actor = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public IReadOnlyDictionary<string, string> Headers { get; } =
        Headers is null
            ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal))
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Headers, StringComparer.Ordinal));
}

public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int EventTypeVersion,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    long GlobalPosition,
    TenantId? TenantId,
    DateTimeOffset OccurredAt,
    IDomainEvent Event,
    EventMetadata Metadata);

public sealed record EventEnvelope<TEvent>(
    Guid EventId,
    string EventType,
    int EventTypeVersion,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    long GlobalPosition,
    TenantId? TenantId,
    DateTimeOffset OccurredAt,
    TEvent Event,
    EventMetadata Metadata)
    where TEvent : IDomainEvent;
