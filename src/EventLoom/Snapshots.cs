using System.Text.Json;

namespace EventLoom;

/// <summary>
/// Identifies the aggregate whose state a snapshot represents. Only
/// <see cref="IAggregateSnapshot{TSelf, TAggregate}"/> can implement this interface.
/// </summary>
/// <typeparam name="TAggregate">The aggregate whose state this snapshot represents.</typeparam>
public interface IAggregateSnapshot<TAggregate>
    where TAggregate : Aggregate
{
    internal static abstract string PersistedName { get; }

    internal static abstract int PersistedVersion { get; }
}

/// <summary>Declares an immutable, versioned DTO used to restore aggregate state.</summary>
/// <remarks>
/// The owning aggregate must implement <see cref="ISnapshotable{TSnapshot}"/> for the snapshot; the compiler
/// rejects a snapshot declaration whose aggregate cannot capture and restore it.
/// </remarks>
/// <typeparam name="TSelf">The implementing snapshot type.</typeparam>
/// <typeparam name="TAggregate">The aggregate whose state this snapshot represents.</typeparam>
public interface IAggregateSnapshot<TSelf, TAggregate> : IAggregateSnapshot<TAggregate>
    where TSelf : IAggregateSnapshot<TSelf, TAggregate>
    where TAggregate : Aggregate, ISnapshotable<TSelf>
{
    /// <summary>Gets the stable persisted snapshot name.</summary>
    static abstract string SnapshotType { get; }

    /// <summary>Gets the positive snapshot schema version. Defaults to 1.</summary>
    static virtual int SnapshotVersion => 1;

    static string IAggregateSnapshot<TAggregate>.PersistedName => TSelf.SnapshotType;

    static int IAggregateSnapshot<TAggregate>.PersistedVersion => TSelf.SnapshotVersion;
}

/// <summary>Captures and restores aggregate state through a snapshot DTO.</summary>
/// <remarks>
/// Implement explicitly so the methods stay off the aggregate's public surface. Never call them directly;
/// EventLoom invokes them when saving and loading. They must not raise events.
/// </remarks>
/// <typeparam name="TSnapshot">The snapshot DTO type.</typeparam>
public interface ISnapshotable<TSnapshot>
{
    /// <summary>Captures the current aggregate state.</summary>
    /// <returns>An immutable snapshot of the aggregate state.</returns>
    TSnapshot CreateSnapshot();

    /// <summary>Replaces aggregate state with the snapshot state.</summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    void RestoreSnapshot(TSnapshot snapshot);
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
internal sealed class SnapshotUpcasterChain
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

/// <summary>Captures and restores aggregate snapshots for one aggregate type.</summary>
internal interface IAggregateSnapshotDispatcher<in TAggregate>
    where TAggregate : Aggregate
{
    string SnapshotType { get; }

    int SchemaVersion { get; }

    string Capture(TAggregate aggregate);

    void Restore(TAggregate aggregate, int schemaVersion, string payload, long streamVersion);
}

/// <summary>Creates snapshot dispatchers from compiler-checked snapshot contracts.</summary>
internal static class AggregateSnapshotDispatcher
{
    public static IAggregateSnapshotDispatcher<TAggregate> Create<TAggregate, TId, TSnapshot>(
        IEnumerable<ISnapshotUpcaster>? upcasters)
        where TAggregate : Aggregate<TAggregate, TId>
        where TSnapshot : IAggregateSnapshot<TAggregate> =>
        new AggregateSnapshotDispatcher<TAggregate, TId, TSnapshot>(upcasters);
}

internal sealed class AggregateSnapshotDispatcher<TAggregate, TId, TSnapshot> : IAggregateSnapshotDispatcher<TAggregate>
    where TAggregate : Aggregate<TAggregate, TId>
    where TSnapshot : IAggregateSnapshot<TAggregate>
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };

    private readonly SnapshotUpcasterChain? upcasterChain;

    public AggregateSnapshotDispatcher(IEnumerable<ISnapshotUpcaster>? upcasters)
    {
        SnapshotType = TSnapshot.PersistedName;
        SchemaVersion = TSnapshot.PersistedVersion;
        if (string.IsNullOrWhiteSpace(SnapshotType))
        {
            throw new InvalidOperationException(
                $"Snapshot type '{typeof(TSnapshot).FullName}' must declare a non-empty SnapshotType.");
        }

        if (SchemaVersion <= 0)
        {
            throw new InvalidOperationException(
                $"Snapshot type '{typeof(TSnapshot).FullName}' must declare a positive SnapshotVersion.");
        }

        upcasterChain = upcasters is null
            ? null
            : new SnapshotUpcasterChain(SnapshotType, upcasters);
    }

    /// <inheritdoc />
    public string SnapshotType { get; }

    /// <inheritdoc />
    public int SchemaVersion { get; }

    /// <inheritdoc />
    public string Capture(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return JsonSerializer.Serialize(aggregate.CaptureSnapshot<TSnapshot>(), DefaultJsonOptions);
    }

    /// <inheritdoc />
    public void Restore(TAggregate aggregate, int schemaVersion, string payload, long streamVersion)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (schemaVersion > SchemaVersion)
        {
            throw new SnapshotIncompatibleException(SnapshotType, schemaVersion, SchemaVersion);
        }

        TSnapshot snapshot;
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
                normalizedPayload = upcasterChain.Upcast(document.RootElement, schemaVersion, SchemaVersion)
                    .GetRawText();
            }

            snapshot = JsonSerializer.Deserialize<TSnapshot>(normalizedPayload, DefaultJsonOptions)
                       ?? throw new SnapshotDeserializationException(SnapshotType);
        }
        catch (JsonException exception)
        {
            throw new SnapshotDeserializationException(SnapshotType, exception);
        }

        aggregate.RestoreSnapshot(snapshot, streamVersion);
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
