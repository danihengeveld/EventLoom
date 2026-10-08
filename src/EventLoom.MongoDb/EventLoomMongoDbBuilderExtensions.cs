using EventLoom.Hosting;
using EventLoom.Storage;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

/// <summary>Configures MongoDB storage for EventLoom.</summary>
public static class EventLoomMongoDbBuilderExtensions
{
    /// <param name="builder">The EventLoom builder to configure.</param>
    extension(EventLoomBuilder builder)
    {
        /// <summary>
        /// Uses MongoDB as the EventLoom storage provider.
        /// </summary>
        /// <param name="connectionString">The MongoDB connection string.</param>
        /// <param name="databaseName">The MongoDB database name EventLoom stores its collections in.</param>
        /// <param name="configure">Optionally configures EventLoom MongoDB storage behavior.</param>
        /// <returns>This builder.</returns>
        public EventLoomBuilder UseMongoDb(
            string connectionString,
            string databaseName,
            Action<MongoDbStorageOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            return UseMongoDb(
                builder,
                _ => new MongoClient(connectionString),
                databaseName,
                configure);
        }

        /// <summary>
        /// Uses a singleton MongoDB client supplied by the application.
        /// </summary>
        /// <param name="clientFactory">Builds the MongoDB client from the application service provider.</param>
        /// <param name="databaseName">The MongoDB database name EventLoom stores its collections in.</param>
        /// <param name="configure">Optionally configures EventLoom MongoDB storage behavior.</param>
        /// <returns>This builder.</returns>
        public EventLoomBuilder UseMongoDb(
            Func<IServiceProvider, IMongoClient> clientFactory,
            string databaseName,
            Action<MongoDbStorageOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(clientFactory);
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

            var options = new MongoDbStorageOptions();
            configure?.Invoke(options);
            options.Validate();
            if (options.RegisterStandardGuidSerializer)
            {
                MongoDocumentMaps.TryRegisterStandardGuidSerializer();
            }

            builder.AddEventStoreRetryPolicy<MongoDbRetryPolicy>();
            return builder.UseStorage(
                new StorageCapabilities(
                    "MongoDB",
                    SupportsTransactionalProjections: true,
                    SupportsInlineProjections: true,
                    SupportsUnitOfWork: true,
                    IsDistributed: true),
                services =>
                {
                    services.AddSingleton(options);
                    services.AddSingleton<Func<IServiceProvider, IMongoClient>>(clientFactory);
                    services.AddSingleton(static serviceProvider =>
                        serviceProvider.GetRequiredService<Func<IServiceProvider, IMongoClient>>()(serviceProvider));
                    services.AddSingleton(serviceProvider => new MongoStorageCatalog(
                        serviceProvider.GetRequiredService<IMongoClient>(),
                        databaseName,
                        serviceProvider.GetRequiredService<MongoDbStorageOptions>()));
                    services.AddSingleton<MongoStorageInitializer>();
                    services.AddScoped(serviceProvider =>
                        new MongoSessionAccessor(serviceProvider.GetRequiredService<MongoStorageCatalog>()));
                    services.AddScoped<IEventStorage, MongoEventStorage>();
                    services.AddScoped<ISnapshotStorage, MongoSnapshotStorage>();
                    services.AddScoped<IProjectionStorage, MongoProjectionStorage>();
                    services.AddScoped<IOutboxStorage, MongoOutboxStorage>();
                    services.AddScoped<IWorkerLeaseStorage, MongoWorkerLeaseStorage>();
                    services.AddScoped<IStorageSchema, MongoStorageSchema>();
                });
        }
    }
}
