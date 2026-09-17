using System.Collections.ObjectModel;
using System.Text.Json;

namespace EventLoom;

public interface IEventUpcaster
{
    string EventName { get; }

    int FromVersion { get; }

    int ToVersion { get; }

    JsonElement Upcast(JsonElement payload);
}

public sealed class EventUpcasterChain
{
    private readonly IReadOnlyList<IEventUpcaster> upcasters;

    public EventUpcasterChain(string eventName, IEnumerable<IEventUpcaster> upcasters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(upcasters);

        var selected = upcasters.Where(upcaster => upcaster.EventName == eventName).ToList();
        Validate(eventName, selected);
        this.upcasters = new ReadOnlyCollection<IEventUpcaster>(selected);
        EventName = eventName;
    }

    public string EventName { get; }

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

public sealed class EventUpcastChainException(string eventName, string reason)
    : InvalidOperationException($"Invalid upcaster chain for event '{eventName}': {reason}");
