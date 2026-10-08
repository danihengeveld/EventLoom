using EventLoom.Storage;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

internal sealed class MongoStorageSchema(
    MongoStorageCatalog catalog,
    MongoStorageInitializer initializer) : IStorageSchema
{
    private readonly MongoStorageCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly MongoStorageInitializer initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        initializer.EnsureCreatedAsync(cancellationToken);

    public async Task<StorageValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await catalog.Database.RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var hello = await catalog.Database.RunCommandAsync<BsonDocument>(
                    new BsonDocument("hello", 1),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var incompatibleCount = IsReplicaSetOrMongos(hello) ? 0 : 1;
            var existingCollectionCursor = await catalog.Database
                .ListCollectionNamesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var existingCollections = (await existingCollectionCursor.ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
            var missingCount = 0;
            foreach (var collection in catalog.ExpectedIndexes)
            {
                if (!existingCollections.Contains(collection.Key))
                {
                    missingCount++;
                    continue;
                }

                var indexCursor = await catalog.Database.GetCollection<BsonDocument>(collection.Key)
                    .Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
                var indexNames = (await indexCursor.ToListAsync(cancellationToken).ConfigureAwait(false))
                    .Select(value => value.GetValue("name", string.Empty).AsString)
                    .ToHashSet(StringComparer.Ordinal);
                missingCount += collection.Value.Count(indexName => !indexNames.Contains(indexName));
            }

            return new StorageValidationResult(true, missingCount, incompatibleCount);
        }
        catch (MongoException)
        {
            return new StorageValidationResult(false, 0, 0);
        }
        catch (TimeoutException)
        {
            return new StorageValidationResult(false, 0, 0);
        }
    }

    private static bool IsReplicaSetOrMongos(BsonDocument hello) =>
        hello.Contains("setName") ||
        string.Equals(hello.GetValue("msg", BsonNull.Value).ToString(), "isdbgrid", StringComparison.Ordinal);
}
