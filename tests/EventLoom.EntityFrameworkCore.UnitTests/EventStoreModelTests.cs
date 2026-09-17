using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EventLoom.UnitTests;

public sealed class EventStoreModelTests
{
    [Test]
    public async Task Event_store_model_uses_deterministic_table_names()
    {
        var modelBuilder = new ModelBuilder();
        modelBuilder.ApplyEventStoreConfiguration(new EventStoreOptions { TablePrefix = "custom_" });

        var tables = modelBuilder.Model.GetEntityTypes()
            .Select(entity => StoreObjectIdentifier.Create(entity, StoreObjectType.Table).GetValueOrDefault().Name)
            .Where(name => name is not null)
            .Order()
            .ToArray();

        await Assert.That(tables).IsEquivalentTo(new[]
        {
            "custom_events",
            "custom_outbox",
            "custom_outbox_attempts",
            "custom_offsets",
            "custom_projection_checkpoints",
            "custom_projection_failures",
            "custom_projection_leases",
            "custom_snapshots",
            "custom_streams"
        });
    }

    [Test]
    public async Task Sqlite_rejects_distributed_worker_mode()
    {
        var capabilities = new SqliteProviderCapabilities();

        await Assert.That(() => capabilities.ValidateWorkerConfiguration(true))
            .Throws<DistributedWorkerConfigurationException>();
    }
}
