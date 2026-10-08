using EventLoom.Storage;

namespace EventLoom.EntityFrameworkCore;

/// <summary>Creates and validates the relational EventLoom schema through an EF Core context.</summary>
internal sealed class EfStorageSchema(EventStoreDbContext context) : IStorageSchema
{
    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        EventStoreSchema.EnsureCreatedAsync(context, cancellationToken);

    public async Task<StorageValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (!await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            return new StorageValidationResult(false, 0, 0);
        }

        var validation = await EventStoreSchema.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        return new StorageValidationResult(
            true,
            validation.MissingTables.Count + validation.MissingColumns.Values.Sum(columns => columns.Count),
            validation.IncompatibleColumns.Values.Sum(columns => columns.Count));
    }
}
