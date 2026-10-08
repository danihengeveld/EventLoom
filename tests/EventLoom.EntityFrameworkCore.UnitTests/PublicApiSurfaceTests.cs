using System.Reflection;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.PostgreSql;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using EventLoom.Storage;

namespace EventLoom.UnitTests;

public sealed class PublicApiSurfaceTests
{
    [Test]
    public async Task Persistence_implementation_types_are_not_exported()
    {
        var storageTypeNames = typeof(EventStore)
            .Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);
        var efTypeNames = typeof(EventStoreDbContext)
            .Assembly
            .GetExportedTypes()
            .Select(type => type.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var typeName in new[]
                 {
                     "EventLoom.Storage.SnapshotStore",
                     "EventLoom.Storage.SnapshotWriteRequest",
                     "EventLoom.Storage.OutboxStore",
                     "EventLoom.Storage.ProjectionStore",
                     "EventLoom.Storage.EventStoreOptions"
                 })
        {
            await Assert.That(storageTypeNames.Contains(typeName)).IsFalse();
        }

        foreach (var typeName in new[]
                 {
                     "EventLoom.EntityFrameworkCore.EventStoreModelBuilderExtensions",
                     "EventLoom.EntityFrameworkCore.EfEventStorage",
                     "EventLoom.EntityFrameworkCore.EfSnapshotStorage",
                     "EventLoom.EntityFrameworkCore.EfProjectionStorage",
                     "EventLoom.EntityFrameworkCore.EfOutboxStorage",
                     "EventLoom.EntityFrameworkCore.EfWorkerLeaseStorage",
                     "EventLoom.EntityFrameworkCore.EfStorageSchema",
                     "EventLoom.EntityFrameworkCore.EfStorageDialect",
                     "EventLoom.EntityFrameworkCore.WorkerLeaseConflictException"
                 })
        {
            await Assert.That(efTypeNames.Contains(typeName)).IsFalse();
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
        var eventStoreOptionProperties = typeof(EntityFrameworkStorageOptions)
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
