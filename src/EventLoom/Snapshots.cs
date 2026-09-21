using System.Text.Json;
using System.Linq.Expressions;
using System.Reflection;

namespace EventLoom;

/// <summary>Marks an immutable, versioned DTO used to restore aggregate state.</summary>
/// <typeparam name="TAggregate">The aggregate whose state this snapshot represents.</typeparam>
public interface IAggregateSnapshot<TAggregate>
    where TAggregate : Aggregate;

/// <summary>Associates a stable persisted name and schema version with an aggregate snapshot DTO.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SnapshotTypeAttribute(string name) : Attribute
{
    /// <summary>Gets the stable persisted snapshot name.</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("Snapshot name is required.", nameof(name))
        : name;

    /// <summary>Gets or sets the positive snapshot schema version.</summary>
    public int Version
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Snapshot schema version must be positive.");
    } = 1;
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

internal sealed class AggregateSnapshotDispatcher<TAggregate>
    where TAggregate : Aggregate
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false
    };

    private readonly Type snapshotType;
    private readonly SnapshotTypeAttribute metadata;
    private readonly Func<TAggregate, object> capture;
    private readonly Action<TAggregate, object> restore;
    private readonly SnapshotUpcasterChain? upcasterChain;

    public AggregateSnapshotDispatcher(Type snapshotType, IEnumerable<ISnapshotUpcaster>? upcasters)
    {
        ArgumentNullException.ThrowIfNull(snapshotType);
        if (!typeof(IAggregateSnapshot<TAggregate>).IsAssignableFrom(snapshotType))
        {
            throw new InvalidOperationException(
                $"Snapshot type '{snapshotType.FullName}' must implement IAggregateSnapshot<{typeof(TAggregate).Name}>.");
        }

        this.snapshotType = snapshotType;
        metadata = snapshotType
        .GetCustomAttributes(typeof(SnapshotTypeAttribute), false)
        .OfType<SnapshotTypeAttribute>()
        .SingleOrDefault() ?? throw new InvalidOperationException(
        $"Snapshot type '{snapshotType.FullName}' is missing SnapshotTypeAttribute.");
        capture = CreateCaptureDelegate(snapshotType);
        restore = CreateRestoreDelegate(snapshotType);
        upcasterChain = upcasters is null
            ? null
            : new SnapshotUpcasterChain(metadata.Name, upcasters);
    }

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
        return JsonSerializer.Serialize(capture(aggregate), snapshotType, DefaultJsonOptions);
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
                normalizedPayload = upcasterChain.Upcast(document.RootElement, schemaVersion, SchemaVersion)
                    .GetRawText();
            }

            var snapshot = JsonSerializer.Deserialize(normalizedPayload, snapshotType, DefaultJsonOptions);
            restore(aggregate, snapshot ?? throw new SnapshotDeserializationException(SnapshotType));
        }
        catch (JsonException exception)
        {
            throw new SnapshotDeserializationException(SnapshotType, exception);
        }
    }

    private static Func<TAggregate, object> CreateCaptureDelegate(Type snapshotType)
    {
        var method = typeof(TAggregate).GetMethod(
            "CreateSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
        if (method is null || method.ReturnType != snapshotType || !method.IsPrivate)
        {
            throw new InvalidOperationException(
                $"Aggregate '{typeof(TAggregate).FullName}' requires a private CreateSnapshot() method that returns '{snapshotType.FullName}'.");
        }

        var aggregate = Expression.Parameter(typeof(TAggregate), "aggregate");
        return Expression.Lambda<Func<TAggregate, object>>(
            Expression.Convert(Expression.Call(aggregate, method), typeof(object)),
            aggregate).Compile();
    }

    private static Action<TAggregate, object> CreateRestoreDelegate(Type snapshotType)
    {
        var method = typeof(TAggregate).GetMethod(
            "RestoreSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [snapshotType],
            null);
        if (method is null || method.ReturnType != typeof(void) || !method.IsPrivate)
        {
            throw new InvalidOperationException(
                $"Aggregate '{typeof(TAggregate).FullName}' requires a private RestoreSnapshot({snapshotType.FullName}) method.");
        }

        var aggregate = Expression.Parameter(typeof(TAggregate), "aggregate");
        var snapshot = Expression.Parameter(typeof(object), "snapshot");
        return Expression.Lambda<Action<TAggregate, object>>(
            Expression.Call(
                aggregate,
                method,
                Expression.Convert(snapshot, snapshotType)),
            aggregate,
            snapshot).Compile();
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
