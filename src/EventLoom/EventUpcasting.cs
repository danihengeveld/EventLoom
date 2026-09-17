using System.Collections.ObjectModel;
using System.Text.Json;

namespace EventLoom;

/// <summary>Transforms one persisted event payload version into its immediate successor.</summary>
public interface IEventUpcaster
{
    /// <summary>Gets the stable event name handled by this upcaster.</summary>
    string EventName { get; }

    /// <summary>Gets the source schema version.</summary>
    int FromVersion { get; }

    /// <summary>Gets the target schema version, which must be exactly one greater than the source.</summary>
    int ToVersion { get; }

    /// <summary>Transforms the source payload into the target payload.</summary>
    /// <param name="payload">The source JSON payload.</param>
    /// <returns>The transformed JSON payload.</returns>
    JsonElement Upcast(JsonElement payload);
}

/// <summary>Validates and executes a deterministic sequence of upcasters for one event name.</summary>
public sealed class EventUpcasterChain
{
    private readonly IReadOnlyList<IEventUpcaster> upcasters;

    /// <summary>Initializes a validated upcaster chain for a stable event name.</summary>
    /// <param name="eventName">The stable event name handled by the chain.</param>
    /// <param name="upcasters">The available upcasters for the event name.</param>
    public EventUpcasterChain(string eventName, IEnumerable<IEventUpcaster> upcasters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(upcasters);

        var selected = upcasters.Where(upcaster => upcaster.EventName == eventName).ToList();
        Validate(eventName, selected);
        this.upcasters = new ReadOnlyCollection<IEventUpcaster>(selected);
        EventName = eventName;
    }

    /// <summary>Gets the stable event name handled by the chain.</summary>
    public string EventName { get; }

    /// <summary>Transforms a payload from its persisted version to a target version.</summary>
    /// <param name="payload">The persisted JSON payload.</param>
    /// <param name="fromVersion">The persisted schema version.</param>
    /// <param name="targetVersion">The current schema version to reach.</param>
    /// <returns>The transformed JSON payload.</returns>
    public JsonElement Upcast(JsonElement payload, int fromVersion, int targetVersion)
    {
        if (fromVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion));
        }

        if (targetVersion < fromVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion), "Target version cannot precede source version.");
        }

        var current = payload;
        for (var version = fromVersion; version < targetVersion; version++)
        {
            var upcaster = upcasters.SingleOrDefault(candidate =>
                candidate.FromVersion == version && candidate.ToVersion == version + 1);
            if (upcaster is null)
            {
                throw new EventUpcastChainException(
                    EventName,
                    $"No upcaster exists from version {version} to {version + 1}.");
            }

            current = upcaster.Upcast(current);
        }

        return current;
    }

    private static void Validate(string eventName, IReadOnlyCollection<IEventUpcaster> upcasters)
    {
        foreach (var upcaster in upcasters)
        {
            if (upcaster.FromVersion <= 0 || upcaster.ToVersion != upcaster.FromVersion + 1)
            {
                throw new EventUpcastChainException(
                    eventName,
                    $"Upcasters must advance exactly one positive version: {upcaster.FromVersion} to {upcaster.ToVersion}.");
            }
        }

        var duplicate = upcasters
            .GroupBy(upcaster => upcaster.FromVersion)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new EventUpcastChainException(
                eventName,
                $"Multiple upcasters are registered from version {duplicate.Key}.");
        }
    }
}

/// <summary>Indicates that an event upcaster chain is incomplete, ambiguous, or invalid.</summary>
public sealed class EventUpcastChainException(string eventName, string reason)
    : InvalidOperationException($"Invalid upcaster chain for event '{eventName}': {reason}");
