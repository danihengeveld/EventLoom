using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EventLoom;

/// <summary>Marks an immutable, versioned DTO used to restore aggregate state.</summary>
public interface IAggregateSnapshot;

/// <summary>Associates a stable persisted name and schema version with an aggregate snapshot DTO.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SnapshotTypeAttribute(string name) : Attribute
{
    private int version = 1;

    /// <summary>Gets the stable persisted snapshot name.</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Snapshot name is required.", nameof(name))
        : name;

    /// <summary>Gets or sets the positive snapshot schema version.</summary>
    public int Version
    {
        get => version;
        init => version = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Snapshot schema version must be positive.");
    }
}

/// <summary>Captures and restores an aggregate using an application-owned versioned snapshot DTO.</summary>
/// <typeparam name="TAggregate">The aggregate type restored by the adapter.</typeparam>
public interface IAggregateSnapshotAdapter<TAggregate>
{
    /// <summary>Gets the stable persisted snapshot type.</summary>
    string SnapshotType { get; }

    /// <summary>Gets the current snapshot schema version.</summary>
    int SchemaVersion { get; }

    /// <summary>Captures aggregate state as a serialized snapshot payload.</summary>
    string Capture(TAggregate aggregate);

    /// <summary>Restores aggregate state from a serialized snapshot payload.</summary>
    void Restore(TAggregate aggregate, int schemaVersion, string payload);
}

/// <summary>Provides a JSON-backed adapter for an explicit aggregate snapshot DTO.</summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
/// <typeparam name="TSnapshot">The immutable application-owned snapshot DTO type.</typeparam>
public sealed class JsonAggregateSnapshotAdapter<TAggregate, TSnapshot>(
    Func<TAggregate, TSnapshot> capture,
    Action<TAggregate, TSnapshot> restore,
    JsonTypeInfo<TSnapshot>? jsonTypeInfo = null) : IAggregateSnapshotAdapter<TAggregate>
    where TSnapshot : IAggregateSnapshot
{
    private readonly Func<TAggregate, TSnapshot> capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly Action<TAggregate, TSnapshot> restore = restore ?? throw new ArgumentNullException(nameof(restore));
    private readonly SnapshotTypeAttribute metadata = typeof(TSnapshot).GetCustomAttributes(typeof(SnapshotTypeAttribute), false)
        .OfType<SnapshotTypeAttribute>()
        .SingleOrDefault() ?? throw new InvalidOperationException(
            $"Snapshot type '{typeof(TSnapshot).FullName}' is missing SnapshotTypeAttribute.");

    /// <inheritdoc />
    public string SnapshotType => metadata.Name;

    /// <inheritdoc />
    public int SchemaVersion => metadata.Version > 0
        ? metadata.Version
        : throw new InvalidOperationException("Snapshot schema version must be positive.");

    /// <inheritdoc />
    public string Capture(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return jsonTypeInfo is null
            ? JsonSerializer.Serialize(capture(aggregate))
            : JsonSerializer.Serialize(capture(aggregate), jsonTypeInfo);
    }

    /// <inheritdoc />
    public void Restore(TAggregate aggregate, int schemaVersion, string payload)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (schemaVersion != SchemaVersion)
        {
            throw new SnapshotIncompatibleException(SnapshotType, schemaVersion, SchemaVersion);
        }

        try
        {
            var snapshot = jsonTypeInfo is null
                ? JsonSerializer.Deserialize<TSnapshot>(payload)
                : JsonSerializer.Deserialize(payload, jsonTypeInfo);
            restore(aggregate, snapshot ?? throw new SnapshotDeserializationException(SnapshotType));
        }
        catch (JsonException exception)
        {
            throw new SnapshotDeserializationException(SnapshotType, exception);
        }
    }
}

/// <summary>Determines whether an aggregate version should be snapshotted.</summary>
public interface ISnapshotPolicy
{
    /// <summary>Returns whether to capture a snapshot at the supplied stream version.</summary>
    bool ShouldSnapshot(long streamVersion);
}

/// <summary>Captures a snapshot every configured number of events.</summary>
public sealed class EveryNEventsSnapshotPolicy(int interval) : ISnapshotPolicy
{
    private readonly int interval = interval > 0 ? interval : throw new ArgumentOutOfRangeException(nameof(interval));

    /// <inheritdoc />
    public bool ShouldSnapshot(long streamVersion) => streamVersion > 0 && streamVersion % interval == 0;
}

/// <summary>Indicates that a snapshot payload cannot be read.</summary>
public sealed class SnapshotDeserializationException(string snapshotType, Exception? innerException = null)
    : InvalidOperationException($"Snapshot '{snapshotType}' could not be deserialized.", innerException);

/// <summary>Indicates that a stored snapshot schema version is incompatible with the configured adapter.</summary>
public sealed class SnapshotIncompatibleException(string snapshotType, int actualVersion, int expectedVersion)
    : InvalidOperationException(
        $"Snapshot '{snapshotType}' version {actualVersion} is incompatible with configured version {expectedVersion}.");
