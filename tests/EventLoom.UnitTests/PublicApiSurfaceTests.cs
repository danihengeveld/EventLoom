using System.Reflection;

namespace EventLoom.UnitTests;

public sealed class PublicApiSurfaceTests
{
    [Test]
    public async Task Aggregate_persistence_bookkeeping_is_not_public()
    {
        var publicMembers = typeof(Aggregate<Guid>)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Select(member => member.Name)
            .ToArray();

        await Assert.That(publicMembers.Contains("PendingEvents")).IsFalse();
        await Assert.That(publicMembers.Contains("ApplyHistory")).IsFalse();
        await Assert.That(publicMembers.Contains("GetPendingEvents")).IsFalse();
        await Assert.That(publicMembers.Contains("ClearPendingEvents")).IsFalse();
    }

    [Test]
    public async Task Core_implementation_types_are_not_exported()
    {
        var exportedTypeNames = typeof(Aggregate)
            .Assembly
            .GetExportedTypes()
            .Select(type => type.FullName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var typeName in new[]
                 {
                     "EventLoom.EventUpcasterChain",
                     "EventLoom.SnapshotUpcasterChain",
                     "EventLoom.IInlineProjectionDispatcher",
                     "EventLoom.ICanonicalIdConverter`1",
                     "EventLoom.StringIdConverter",
                     "EventLoom.GuidIdConverter",
                     "EventLoom.TimeProviderClock",
                     "EventLoom.TenancyMode"
                 })
        {
            await Assert.That(exportedTypeNames.Contains(typeName)).IsFalse();
        }
    }
}
