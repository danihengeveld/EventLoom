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

/// <summary>Transforms one persisted snapshot payload version into its immediate successor.</summary>
public interface ISnapshotUpcaster
{
    /// <summary>Gets the stable snapshot type handled by this upcaster.</summary>
    string SnapshotType { get; }

    /// <summary>Gets the source schema version.</summary>
    int FromVersion { get; }

    /// <summary>Gets the target schema version, which must be exactly one greater than the source.</summary>
    int ToVersion { get; }

    /// <summary>Transforms the source snapshot payload into the target payload.</summary>
    /// <param name="payload">The source JSON payload.</param>
    /// <returns>The transformed JSON payload.</returns>
    JsonElement Upcast(JsonElement payload);
}

/// <summary>Validates and executes a deterministic sequence of upcasters for one snapshot type.</summary>
public sealed class SnapshotUpcasterChain
{
    private readonly IReadOnlyList<ISnapshotUpcaster> upcasters;

    /// <summary>Initializes a validated upcaster chain for a stable snapshot type.</summary>
    /// <param name="snapshotType">The stable snapshot type handled by the chain.</param>
    /// <param name="upcasters">The available upcasters for the snapshot type.</param>
    public SnapshotUpcasterChain(string snapshotType, IEnumerable<ISnapshotUpcaster> upcasters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotType);
        ArgumentNullException.ThrowIfNull(upcasters);
        var selected = upcasters.Where(value => value.SnapshotType == snapshotType).ToArray();
        foreach (var upcaster in selected)
        {
            if (upcaster.FromVersion <= 0 || upcaster.ToVersion != upcaster.FromVersion + 1)
            {
                throw new SnapshotUpcastException(
                    snapshotType,
                    $"Upcasters must advance exactly one positive version: {upcaster.FromVersion} to {upcaster.ToVersion}.");
            }
        }

        if (selected.GroupBy(value => value.FromVersion).Any(group => group.Count() > 1))
        {
            throw new SnapshotUpcastException(snapshotType, "Multiple upcasters begin at the same version.");
        }

        SnapshotType = snapshotType;
        this.upcasters = selected;
    }

    /// <summary>Gets the stable snapshot type handled by the chain.</summary>
    public string SnapshotType { get; }

    /// <summary>Transforms a payload from its persisted schema version to the target version.</summary>
    /// <param name="payload">The persisted JSON payload.</param>
    /// <param name="fromVersion">The persisted schema version.</param>
    /// <param name="targetVersion">The current schema version to reach.</param>
    /// <returns>The transformed JSON payload.</returns>
    public JsonElement Upcast(JsonElement payload, int fromVersion, int targetVersion)
    {
        var current = payload;
        for (var version = fromVersion; version < targetVersion; version++)
        {
            var upcaster = upcasters.SingleOrDefault(value =>
                value.FromVersion == version && value.ToVersion == version + 1)
                ?? throw new SnapshotUpcastException(
                    SnapshotType,
                    $"No upcaster exists from version {version} to {version + 1}.");
            try
            {
                current = upcaster.Upcast(current);
            }
            catch (SnapshotUpcastException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new SnapshotUpcastException(
                    SnapshotType,
                    $"The upcaster from version {version} to {version + 1} failed: {exception.GetType().Name}.");
            }
        }

        return current;
    }
}

/// <summary>Provides an adapter for an explicit aggregate snapshot DTO.</summary>
/// <typeparam name="TAggregate">The aggregate type.</typeparam>
/// <typeparam name="TSnapshot">The immutable application-owned snapshot DTO type.</typeparam>
public sealed class AggregateSnapshotAdapter<TAggregate, TSnapshot>(
    Func<TAggregate, TSnapshot> capture,
    Action<TAggregate, TSnapshot> restore,
    JsonTypeInfo<TSnapshot>? jsonTypeInfo = null,
    IEnumerable<ISnapshotUpcaster>? upcasters = null) : IAggregateSnapshotAdapter<TAggregate>
    where TSnapshot : IAggregateSnapshot
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };
    private readonly Func<TAggregate, TSnapshot> capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly Action<TAggregate, TSnapshot> restore = restore ?? throw new ArgumentNullException(nameof(restore));
    private readonly SnapshotTypeAttribute metadata = typeof(TSnapshot).GetCustomAttributes(typeof(SnapshotTypeAttribute), false)
        .OfType<SnapshotTypeAttribute>()
        .SingleOrDefault() ?? throw new InvalidOperationException(
            $"Snapshot type '{typeof(TSnapshot).FullName}' is missing SnapshotTypeAttribute.");
    private readonly SnapshotUpcasterChain? upcasterChain = upcasters is null
        ? null
        : new SnapshotUpcasterChain(
            typeof(TSnapshot).GetCustomAttributes(typeof(SnapshotTypeAttribute), false)
                .OfType<SnapshotTypeAttribute>()
                .SingleOrDefault()?.Name
                ?? throw new InvalidOperationException(
                    $"Snapshot type '{typeof(TSnapshot).FullName}' is missing SnapshotTypeAttribute."),
            upcasters);

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
            ? JsonSerializer.Serialize(capture(aggregate), DefaultJsonOptions)
            : JsonSerializer.Serialize(capture(aggregate), jsonTypeInfo);
    }

    /// <inheritdoc />
    public void Restore(TAggregate aggregate, int schemaVersion, string payload)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (schemaVersion > SchemaVersion)
        {
            throw new SnapshotIncompatibleException(SnapshotType, schemaVersion, SchemaVersion);
        }

        try
        {
            var normalizedPayload = payload;
            if (schemaVersion < SchemaVersion)
            {
                if (upcasterChain is null)
                {
                    throw new SnapshotIncompatibleException(SnapshotType, schemaVersion, SchemaVersion);
                }

                using var document = JsonDocument.Parse(payload);
                normalizedPayload = upcasterChain.Upcast(document.RootElement, schemaVersion, SchemaVersion).GetRawText();
            }
            var snapshot = jsonTypeInfo is null
                ? JsonSerializer.Deserialize<TSnapshot>(normalizedPayload, DefaultJsonOptions)
                : JsonSerializer.Deserialize(normalizedPayload, jsonTypeInfo);
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

/// <summary>Determines how many of an aggregate stream's most recent snapshots to retain.</summary>
public interface ISnapshotRetentionPolicy
{
    /// <summary>Gets the positive number of most recent snapshots to retain.</summary>
    int SnapshotsToRetain { get; }
}

/// <summary>Retains a fixed positive number of the most recent snapshots for each aggregate stream.</summary>
public sealed class KeepLatestSnapshotsPolicy(int snapshotsToRetain) : ISnapshotRetentionPolicy
{
    /// <inheritdoc />
    public int SnapshotsToRetain { get; } = snapshotsToRetain > 0
        ? snapshotsToRetain
        : throw new ArgumentOutOfRangeException(
            nameof(snapshotsToRetain),
            "At least one snapshot must be retained.");
}

/// <summary>Describes why EventLoom could not restore a persisted snapshot.</summary>
public enum SnapshotInvalidationReason
{
    /// <summary>The snapshot JSON payload could not be deserialized.</summary>
    Corrupt,

    /// <summary>The persisted snapshot schema version is not supported by the configured adapter.</summary>
    Incompatible,

    /// <summary>A required snapshot payload transformation could not complete.</summary>
    UpcastFailed
}

/// <summary>
/// Optionally chooses whether an unusable snapshot should be removed after EventLoom safely falls back to full replay.
/// </summary>
public interface ISnapshotInvalidator
{
    /// <summary>Returns whether the unusable snapshot should be removed.</summary>
    /// <param name="snapshotType">The stable persisted snapshot type.</param>
    /// <param name="schemaVersion">The persisted snapshot schema version.</param>
    /// <param name="reason">Why the snapshot could not be restored.</param>
    bool ShouldInvalidate(string snapshotType, int schemaVersion, SnapshotInvalidationReason reason);
}

/// <summary>Indicates that a snapshot payload cannot be read.</summary>
public sealed class SnapshotDeserializationException(string snapshotType, Exception? innerException = null)
    : InvalidOperationException($"Snapshot '{snapshotType}' could not be deserialized.", innerException);

/// <summary>Indicates that a stored snapshot schema version is incompatible with the configured adapter.</summary>
public sealed class SnapshotIncompatibleException(string snapshotType, int actualVersion, int expectedVersion)
    : InvalidOperationException(
        $"Snapshot '{snapshotType}' version {actualVersion} is incompatible with configured version {expectedVersion}.");

/// <summary>Indicates that a snapshot upcaster chain is incomplete, ambiguous, or invalid.</summary>
public sealed class SnapshotUpcastException(string snapshotType, string reason)
    : InvalidOperationException($"Invalid snapshot upcaster chain for '{snapshotType}': {reason}");
