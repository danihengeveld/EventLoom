using System.Collections.ObjectModel;

namespace EventLoom;

/// <summary>Contains operational metadata associated with a persisted event.</summary>
public sealed record EventMetadata(
    string? CorrelationId = null,
    string? CausationId = null,
    string? Actor = null,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    /// <summary>
    /// Gets immutable application-defined operational headers. The collection is empty when no headers were supplied.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; } =
        Headers is null
            ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal))
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Headers, StringComparer.Ordinal));
}

/// <summary>Contains a domain event and its immutable persistence envelope.</summary>
public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int EventTypeVersion,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    long TenantOffset,
    TenantId? TenantId,
    DateTimeOffset OccurredAt,
    IDomainEvent Event,
    EventMetadata Metadata);

/// <summary>Strongly typed event envelope for projection and application handlers.</summary>
public sealed record EventEnvelope<TEvent>(
    Guid EventId,
    string EventType,
    int EventTypeVersion,
    string StreamId,
    string AggregateType,
    long StreamVersion,
    long TenantOffset,
    TenantId? TenantId,
    DateTimeOffset OccurredAt,
    TEvent Event,
    EventMetadata Metadata)
    where TEvent : IDomainEvent;
