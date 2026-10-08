using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EventLoom.EntityFrameworkCore.PostgreSql;

/// <summary>PostgreSQL strategy for gapless tenant offsets, conditional lease updates and server-side filtering.</summary>
internal sealed class PostgreSqlStorageDialect : EfStorageDialect
{
    public override bool UsesConditionalLeaseUpdate => true;

    public override bool FiltersPublishedOutboxInDatabase => true;

    [SuppressMessage(
        "Usage",
        "EF1003:Interpolated SQL queries should use the interpolated form",
        Justification =
            "The table identifier comes from EF's mapped model and is quoted; tenant values remain parameters.")]
    public override async Task<TenantOffsetEntity?> LockTenantOffsetAsync(
        EventStoreDbContext context,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var entityType = context.Model.FindEntityType(typeof(TenantOffsetEntity))
                         ?? throw new InvalidOperationException("The tenant offset entity is not mapped.");
        var table = QuoteIdentifier(entityType.GetTableName()
                                    ?? throw new InvalidOperationException("The tenant offset table is not mapped."));
        var schema = entityType.GetSchema();
        var qualifiedTable = schema is null ? table : $"{QuoteIdentifier(schema)}.{table}";

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction()
                              ?? throw new InvalidOperationException(
                                  "A transaction is required to lock tenant offsets.");
        command.CommandText = "INSERT INTO " + qualifiedTable +
                              " (\"TenantId\", \"NextOffset\") VALUES (@tenantId, 0) " +
                              "ON CONFLICT (\"TenantId\") DO UPDATE SET \"NextOffset\" = " +
                              table + ".\"NextOffset\" RETURNING \"NextOffset\"";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenantId";
        parameter.Value = tenantId;
        command.Parameters.Add(parameter);
        var currentOffset = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (currentOffset is not long value)
        {
            throw new InvalidOperationException("The tenant offset lock did not return an offset.");
        }

        var offset = new TenantOffsetEntity { TenantId = tenantId, NextOffset = value };
        context.TenantOffsets.Attach(offset);
        return offset;
    }

    private static string QuoteIdentifier(string identifier) =>
        $@"""{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}""";
}
