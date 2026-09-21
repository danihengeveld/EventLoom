using System.Reflection;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;

namespace EventLoom.UnitTests;

public sealed class PublicApiSurfaceTests
{
    [Test]
    public async Task Persistence_implementation_types_are_not_exported()
    {
        var exportedTypeNames = typeof(EventStore)
            .Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var typeName in new[]
                 {
                     "EventLoom.EntityFrameworkCore.EventStoreModelBuilderExtensions",
                     "EventLoom.EntityFrameworkCore.SnapshotStore",
                     "EventLoom.EntityFrameworkCore.SnapshotWriteRequest",
                     "EventLoom.EntityFrameworkCore.SnapshotEnvelope",
                     "EventLoom.EntityFrameworkCore.WorkerLeaseStore",
                     "EventLoom.EntityFrameworkCore.WorkerLease",
                     "EventLoom.EntityFrameworkCore.WorkerLeaseConflictException",
                     "EventLoom.EntityFrameworkCore.OutboxStore",
                     "EventLoom.EntityFrameworkCore.OutboxLeaseLostException",
                     "EventLoom.EntityFrameworkCore.ProjectionStore",
                     "EventLoom.EntityFrameworkCore.ProjectionDeliveryResult",
                     "EventLoom.EntityFrameworkCore.ProjectionLeaseLostException"
                 })
        {
            await Assert.That(exportedTypeNames.Contains(typeName)).IsFalse();
        }
    }

    [Test]
    public async Task Provider_implementation_types_are_not_exported()
    {
        var postgreSqlTypes = typeof(EventLoomPostgreSqlBuilderExtensions)
            .Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);
        var sqliteTypes = typeof(EventLoomSqliteBuilderExtensions)
            .Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(postgreSqlTypes.Contains(
            "EventLoom.EntityFrameworkCore.PostgreSql.EventLoomPostgreSqlBuilderExtensions")).IsTrue();
        await Assert.That(postgreSqlTypes.Contains(
            "EventLoom.EntityFrameworkCore.PostgreSql.PostgreSqlExceptionClassifier")).IsFalse();
        await Assert.That(postgreSqlTypes.Contains(
            "EventLoom.EntityFrameworkCore.PostgreSql.PostgreSqlRetryPolicy")).IsFalse();
        await Assert.That(postgreSqlTypes.Contains(
            "EventLoom.EntityFrameworkCore.PostgreSql.PostgreSqlEventStoreSchema")).IsFalse();
        await Assert.That(postgreSqlTypes.Contains(
            "EventLoom.EntityFrameworkCore.PostgreSql.PostgreSqlProviderCapabilities")).IsFalse();
        await Assert.That(sqliteTypes.Contains(
            "EventLoom.EntityFrameworkCore.Sqlite.EventLoomSqliteBuilderExtensions")).IsTrue();
        await Assert.That(sqliteTypes.Contains(
            "EventLoom.EntityFrameworkCore.Sqlite.SqliteEventStoreSchema")).IsFalse();
        await Assert.That(sqliteTypes.Contains(
            "EventLoom.EntityFrameworkCore.Sqlite.SqliteProviderCapabilities")).IsFalse();
    }

    [Test]
    public async Task Configuration_exposes_supported_entry_points_only()
    {
        var eventStoreOptionProperties = typeof(EventStoreOptions)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        var builderMethods = typeof(EventLoomBuilder)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        await Assert.That(eventStoreOptionProperties).IsEquivalentTo(["Schema", "TablePrefix"]);
        await Assert.That(builderMethods.Contains("ConfigureTenancy")).IsFalse();
        await Assert.That(builderMethods.Contains("UseMultiTenancy")).IsTrue();
    }
}
