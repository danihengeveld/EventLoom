using EventLoom.Storage;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace EventLoom.MongoDb;

internal sealed class MongoStorageCatalog
{
    private const string StreamsName = "streams";
    private const string EventsName = "events";
    private const string OffsetsName = "offsets";
    private const string SnapshotsName = "snapshots";
    private const string ProjectionCheckpointsName = "projection_checkpoints";
    private const string ProjectionFailuresName = "projection_failures";
    private const string ProjectionLeasesName = "projection_leases";
    private const string OutboxName = "outbox";
    private const string OutboxAttemptsName = "outbox_attempts";

    public MongoStorageCatalog(IMongoClient client, string databaseName, MongoDbStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(options);

        MongoDocumentMaps.EnsureRegistered();
        Client = client;
        Database = client.GetDatabase(databaseName);
        Options = options;
        StreamsCollectionName = options.CollectionPrefix + StreamsName;
        EventsCollectionName = options.CollectionPrefix + EventsName;
        OffsetsCollectionName = options.CollectionPrefix + OffsetsName;
        SnapshotsCollectionName = options.CollectionPrefix + SnapshotsName;
        ProjectionCheckpointsCollectionName = options.CollectionPrefix + ProjectionCheckpointsName;
        ProjectionFailuresCollectionName = options.CollectionPrefix + ProjectionFailuresName;
        ProjectionLeasesCollectionName = options.CollectionPrefix + ProjectionLeasesName;
        OutboxCollectionName = options.CollectionPrefix + OutboxName;
        OutboxAttemptsCollectionName = options.CollectionPrefix + OutboxAttemptsName;
        Streams = Database.GetCollection<StreamDocument>(StreamsCollectionName);
        Events = Database.GetCollection<EventDocument>(EventsCollectionName);
        Offsets = Database.GetCollection<OffsetDocument>(OffsetsCollectionName);
        Snapshots = Database.GetCollection<SnapshotDocument>(SnapshotsCollectionName);
        ProjectionCheckpoints = Database.GetCollection<ProjectionCheckpointDocument>(ProjectionCheckpointsCollectionName);
        ProjectionFailures = Database.GetCollection<ProjectionFailureDocument>(ProjectionFailuresCollectionName);
        ProjectionLeases = Database.GetCollection<ProjectionLeaseDocument>(ProjectionLeasesCollectionName);
        Outbox = Database.GetCollection<OutboxDocument>(OutboxCollectionName);
        OutboxAttempts = Database.GetCollection<OutboxAttemptDocument>(OutboxAttemptsCollectionName);
        TransactionOptions = new TransactionOptions(
            readConcern: ReadConcern.Snapshot,
            writeConcern: WriteConcern.WMajority,
            maxCommitTime: options.TransactionTimeout);
    }

    public IMongoClient Client { get; }

    public IMongoDatabase Database { get; }

    public MongoDbStorageOptions Options { get; }

    public TransactionOptions TransactionOptions { get; }

    public IMongoCollection<StreamDocument> Streams { get; }

    public IMongoCollection<EventDocument> Events { get; }

    public IMongoCollection<OffsetDocument> Offsets { get; }

    public IMongoCollection<SnapshotDocument> Snapshots { get; }

    public IMongoCollection<ProjectionCheckpointDocument> ProjectionCheckpoints { get; }

    public IMongoCollection<ProjectionFailureDocument> ProjectionFailures { get; }

    public IMongoCollection<ProjectionLeaseDocument> ProjectionLeases { get; }

    public IMongoCollection<OutboxDocument> Outbox { get; }

    public IMongoCollection<OutboxAttemptDocument> OutboxAttempts { get; }

    public string StreamsCollectionName { get; }

    public string EventsCollectionName { get; }

    public string OffsetsCollectionName { get; }

    public string SnapshotsCollectionName { get; }

    public string ProjectionCheckpointsCollectionName { get; }

    public string ProjectionFailuresCollectionName { get; }

    public string ProjectionLeasesCollectionName { get; }

    public string OutboxCollectionName { get; }

    public string OutboxAttemptsCollectionName { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedIndexes => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [StreamsCollectionName] = ["ux_streams_tenant_stream"],
        [EventsCollectionName] =
        [
            "ux_events_tenant_stream_version",
            "ux_events_tenant_offset",
            "ux_events_tenant_event",
            "ix_events_tenant_append"
        ],
        [OffsetsCollectionName] = [],
        [SnapshotsCollectionName] = ["ix_snapshots_tenant_stream_aggregate_version"],
        [ProjectionCheckpointsCollectionName] =
        [
            "ux_projection_checkpoints_tenant_name_version",
            "ix_projection_checkpoints_tenant_status_updated_at"
        ],
        [ProjectionFailuresCollectionName] =
        [
            "ix_projection_failures_tenant_projection_resolved_offset",
            "ux_projection_failures_tenant_projection_event_resolved"
        ],
        [ProjectionLeasesCollectionName] = ["ux_projection_leases_tenant_name"],
        [OutboxCollectionName] =
        [
            "ux_outbox_tenant_message",
            "ix_outbox_tenant_published_offset"
        ],
        [OutboxAttemptsCollectionName] =
        [
            "ux_outbox_attempts_tenant_message_attempt",
            "ux_outbox_attempts_message_attempt",
            "ix_outbox_attempts_tenant_message"
        ]
    };
}

internal sealed class MongoStorageInitializer(MongoStorageCatalog catalog)
{
    private readonly MongoStorageCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            var existingCollectionCursor = await catalog.Database
                .ListCollectionNamesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var existingCollections = (await existingCollectionCursor.ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var collectionName in catalog.ExpectedIndexes.Keys)
            {
                if (!existingCollections.Contains(collectionName))
                {
                    try
                    {
                        await catalog.Database.CreateCollectionAsync(collectionName, cancellationToken: cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (MongoCommandException exception) when (exception.Code == 48)
                    {
                    }
                }
            }

            await CreateIndexesAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CreateIndexesAsync(CancellationToken cancellationToken)
    {
        await catalog.Streams.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<StreamDocument>(
                    Builders<StreamDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.StreamId),
                    new CreateIndexOptions { Name = "ux_streams_tenant_stream", Unique = true })
            ],
            cancellationToken).ConfigureAwait(false);

        await catalog.Events.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<EventDocument>(
                    Builders<EventDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.StreamId)
                        .Ascending(value => value.StreamVersion),
                    new CreateIndexOptions { Name = "ux_events_tenant_stream_version", Unique = true }),
                new CreateIndexModel<EventDocument>(
                    Builders<EventDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.TenantOffset),
                    new CreateIndexOptions { Name = "ux_events_tenant_offset", Unique = true }),
                new CreateIndexModel<EventDocument>(
                    Builders<EventDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.EventId),
                    new CreateIndexOptions { Name = "ux_events_tenant_event", Unique = true }),
                new CreateIndexModel<EventDocument>(
                    Builders<EventDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.AppendId),
                    new CreateIndexOptions<EventDocument>
                    {
                        Name = "ix_events_tenant_append",
                        PartialFilterExpression = new BsonDocument("appendId", new BsonDocument("$exists", true))
                    })
            ],
            cancellationToken).ConfigureAwait(false);

        await catalog.Snapshots.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<SnapshotDocument>(
                    Builders<SnapshotDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.StreamId)
                        .Ascending(value => value.AggregateType)
                        .Ascending(value => value.StreamVersion),
                    new CreateIndexOptions { Name = "ix_snapshots_tenant_stream_aggregate_version" })
            ],
            cancellationToken).ConfigureAwait(false);

        await catalog.ProjectionCheckpoints.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ProjectionCheckpointDocument>(
                    Builders<ProjectionCheckpointDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.ProjectionName)
                        .Ascending(value => value.ProjectionVersion),
                    new CreateIndexOptions
                    {
                        Name = "ux_projection_checkpoints_tenant_name_version",
                        Unique = true
                    }),
                new CreateIndexModel<ProjectionCheckpointDocument>(
                    Builders<ProjectionCheckpointDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.Status)
                        .Ascending(value => value.UpdatedAtTicks),
                    new CreateIndexOptions { Name = "ix_projection_checkpoints_tenant_status_updated_at" })
            ],
            cancellationToken).ConfigureAwait(false);

        await catalog.ProjectionFailures.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ProjectionFailureDocument>(
                    Builders<ProjectionFailureDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.ProjectionName)
                        .Ascending(value => value.ProjectionVersion)
                        .Ascending(value => value.ResolvedAtTicks)
                        .Ascending(value => value.TenantOffset),
                    new CreateIndexOptions { Name = "ix_projection_failures_tenant_projection_resolved_offset" }),
                new CreateIndexModel<ProjectionFailureDocument>(
                    Builders<ProjectionFailureDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.ProjectionName)
                        .Ascending(value => value.ProjectionVersion)
                        .Ascending(value => value.EventId)
                        .Ascending(value => value.ResolvedAtTicks),
                    new CreateIndexOptions
                    {
                        Name = "ux_projection_failures_tenant_projection_event_resolved",
                        Unique = true
                    })
            ],
            cancellationToken).ConfigureAwait(false);

        await catalog.ProjectionLeases.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ProjectionLeaseDocument>(
                    Builders<ProjectionLeaseDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.LeaseName),
                    new CreateIndexOptions { Name = "ux_projection_leases_tenant_name", Unique = true })
            ],
            cancellationToken).ConfigureAwait(false);

        await catalog.Outbox.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<OutboxDocument>(
                    Builders<OutboxDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.MessageId),
                    new CreateIndexOptions { Name = "ux_outbox_tenant_message", Unique = true }),
                new CreateIndexModel<OutboxDocument>(
                    Builders<OutboxDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.PublishedAtTicks)
                        .Ascending(value => value.TenantOffset),
                    new CreateIndexOptions { Name = "ix_outbox_tenant_published_offset" })
            ],
            cancellationToken).ConfigureAwait(false);

        await catalog.OutboxAttempts.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<OutboxAttemptDocument>(
                    Builders<OutboxAttemptDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.MessageId)
                        .Ascending(value => value.AttemptNumber),
                    new CreateIndexOptions
                    {
                        Name = "ux_outbox_attempts_tenant_message_attempt",
                        Unique = true
                    }),
                new CreateIndexModel<OutboxAttemptDocument>(
                    Builders<OutboxAttemptDocument>.IndexKeys
                        .Ascending(value => value.MessageId)
                        .Ascending(value => value.AttemptNumber),
                    new CreateIndexOptions { Name = "ux_outbox_attempts_message_attempt", Unique = true }),
                new CreateIndexModel<OutboxAttemptDocument>(
                    Builders<OutboxAttemptDocument>.IndexKeys
                        .Ascending(value => value.TenantId)
                        .Ascending(value => value.MessageId),
                    new CreateIndexOptions { Name = "ix_outbox_attempts_tenant_message" })
            ],
            cancellationToken).ConfigureAwait(false);
    }
}

internal static class MongoStorageTime
{
    public static long ToUtcTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    public static long? ToUtcTicks(DateTimeOffset? value) => value is null ? null : value.Value.UtcDateTime.Ticks;

    public static DateTimeOffset FromUtcTicks(long value) => new(value, TimeSpan.Zero);

    public static DateTimeOffset? FromUtcTicks(long? value) => value is null ? null : new DateTimeOffset(value.Value, TimeSpan.Zero);
}

internal static class MongoMetadata
{
    public static Dictionary<string, string>? ToDocument(IReadOnlyDictionary<string, string> headers) =>
        headers.Count == 0 ? null : new Dictionary<string, string>(headers, StringComparer.Ordinal);
}

internal sealed class StreamDocument
{
    public ObjectId Id { get; set; }

    public string TenantId { get; set; } = null!;

    public string StreamId { get; set; } = null!;

    public string AggregateType { get; set; } = null!;

    public long Version { get; set; }
}

internal sealed class EventDocument
{
    public ObjectId Id { get; set; }

    public Guid EventId { get; set; }

    public string TenantId { get; set; } = null!;

    public string StreamId { get; set; } = null!;

    public string AggregateType { get; set; } = null!;

    public long StreamVersion { get; set; }

    public long TenantOffset { get; set; }

    public string EventType { get; set; } = null!;

    public int EventTypeVersion { get; set; }

    public string Payload { get; set; } = null!;

    public long OccurredAtTicks { get; set; }

    public string? CorrelationId { get; set; }

    public string? CausationId { get; set; }

    public string? Actor { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? AppendId { get; set; }
}

internal sealed class OffsetDocument
{
    public string Id { get; set; } = null!;

    public string TenantId { get; set; } = null!;

    public long NextOffset { get; set; }
}

internal sealed class SnapshotDocument
{
    public ObjectId Id { get; set; }

    public string TenantId { get; set; } = null!;

    public string StreamId { get; set; } = null!;

    public string AggregateType { get; set; } = null!;

    public long StreamVersion { get; set; }

    public string SnapshotType { get; set; } = null!;

    public int SchemaVersion { get; set; }

    public string Payload { get; set; } = null!;

    public long CreatedAtTicks { get; set; }
}

internal sealed class ProjectionCheckpointDocument
{
    public ObjectId Id { get; set; }

    public string TenantId { get; set; } = null!;

    public string ProjectionName { get; set; } = null!;

    public int ProjectionVersion { get; set; }

    public long TenantOffset { get; set; }

    public ProjectionStatus Status { get; set; }

    public long UpdatedAtTicks { get; set; }
}

internal sealed class ProjectionFailureDocument
{
    public ObjectId Id { get; set; }

    public string TenantId { get; set; } = null!;

    public string ProjectionName { get; set; } = null!;

    public int ProjectionVersion { get; set; }

    public Guid EventId { get; set; }

    public string EventType { get; set; } = null!;

    public long TenantOffset { get; set; }

    public int AttemptCount { get; set; }

    public string ExceptionType { get; set; } = null!;

    public long FailedAtTicks { get; set; }

    public long? ResolvedAtTicks { get; set; }

    public bool WasSkipped { get; set; }
}

internal sealed class ProjectionLeaseDocument
{
    public ObjectId Id { get; set; }

    public string TenantId { get; set; } = null!;

    public string LeaseName { get; set; } = null!;

    public string OwnerId { get; set; } = null!;

    public long FencingToken { get; set; }

    public long LeaseUntilTicks { get; set; }
}

internal sealed class OutboxDocument
{
    public ObjectId Id { get; set; }

    public Guid MessageId { get; set; }

    public string TenantId { get; set; } = null!;

    public string StreamId { get; set; } = null!;

    public string AggregateType { get; set; } = null!;

    public long StreamVersion { get; set; }

    public long TenantOffset { get; set; }

    public string EventType { get; set; } = null!;

    public int EventTypeVersion { get; set; }

    public string Payload { get; set; } = null!;

    public long OccurredAtTicks { get; set; }

    public string? CorrelationId { get; set; }

    public string? CausationId { get; set; }

    public string? Actor { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public int AttemptCount { get; set; }

    public long? PublishedAtTicks { get; set; }
}

internal sealed class OutboxAttemptDocument
{
    public ObjectId Id { get; set; }

    public Guid MessageId { get; set; }

    public string TenantId { get; set; } = null!;

    public int AttemptNumber { get; set; }

    public long AttemptedAtTicks { get; set; }

    public bool Succeeded { get; set; }

    public string? ExceptionType { get; set; }
}

internal static class MongoDocumentMaps
{
    private static readonly Lock RegistrationLock = new();
    private static volatile bool registered;

    public static void EnsureRegistered()
    {
        if (registered)
        {
            return;
        }

        lock (RegistrationLock)
        {
            if (registered)
            {
                return;
            }

            RegisterAll();
            registered = true;
        }
    }

    public static void TryRegisterStandardGuidSerializer()
    {
        try
        {
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        }
        catch (BsonSerializationException)
        {
            // A different Guid serializer is already registered; the application owns that choice.
        }
    }

    private static void RegisterAll()
    {
        RegisterStream();
        RegisterEvent();
        RegisterOffset();
        RegisterSnapshot();
        RegisterProjectionCheckpoint();
        RegisterProjectionFailure();
        RegisterProjectionLease();
        RegisterOutbox();
        RegisterOutboxAttempt();
    }

    private static void RegisterStream() => Register<StreamDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.StreamId).SetElementName("streamId");
            map.MapMember(value => value.AggregateType).SetElementName("aggregateType");
            map.MapMember(value => value.Version).SetElementName("version");
        });

    private static void RegisterEvent() => Register<EventDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.EventId)
                .SetElementName("eventId")
                .SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.StreamId).SetElementName("streamId");
            map.MapMember(value => value.AggregateType).SetElementName("aggregateType");
            map.MapMember(value => value.StreamVersion).SetElementName("streamVersion");
            map.MapMember(value => value.TenantOffset).SetElementName("tenantOffset");
            map.MapMember(value => value.EventType).SetElementName("eventType");
            map.MapMember(value => value.EventTypeVersion).SetElementName("eventTypeVersion");
            map.MapMember(value => value.Payload).SetElementName("payload");
            map.MapMember(value => value.OccurredAtTicks).SetElementName("occurredAtTicks");
            map.MapMember(value => value.CorrelationId).SetElementName("correlationId").SetIgnoreIfNull(true);
            map.MapMember(value => value.CausationId).SetElementName("causationId").SetIgnoreIfNull(true);
            map.MapMember(value => value.Actor).SetElementName("actor").SetIgnoreIfNull(true);
            map.MapMember(value => value.Headers).SetElementName("headers").SetIgnoreIfNull(true);
            map.MapMember(value => value.AppendId).SetElementName("appendId").SetIgnoreIfNull(true);
        });

    private static void RegisterOffset() => Register<OffsetDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.NextOffset).SetElementName("nextOffset");
        });

    private static void RegisterSnapshot() => Register<SnapshotDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.StreamId).SetElementName("streamId");
            map.MapMember(value => value.AggregateType).SetElementName("aggregateType");
            map.MapMember(value => value.StreamVersion).SetElementName("streamVersion");
            map.MapMember(value => value.SnapshotType).SetElementName("snapshotType");
            map.MapMember(value => value.SchemaVersion).SetElementName("schemaVersion");
            map.MapMember(value => value.Payload).SetElementName("payload");
            map.MapMember(value => value.CreatedAtTicks).SetElementName("createdAtTicks");
        });

    private static void RegisterProjectionCheckpoint() => Register<ProjectionCheckpointDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.ProjectionName).SetElementName("projectionName");
            map.MapMember(value => value.ProjectionVersion).SetElementName("projectionVersion");
            map.MapMember(value => value.TenantOffset).SetElementName("tenantOffset");
            map.MapMember(value => value.Status).SetElementName("status");
            map.MapMember(value => value.UpdatedAtTicks).SetElementName("updatedAtTicks");
        });

    private static void RegisterProjectionFailure() => Register<ProjectionFailureDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.ProjectionName).SetElementName("projectionName");
            map.MapMember(value => value.ProjectionVersion).SetElementName("projectionVersion");
            map.MapMember(value => value.EventId)
                .SetElementName("eventId")
                .SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
            map.MapMember(value => value.EventType).SetElementName("eventType");
            map.MapMember(value => value.TenantOffset).SetElementName("tenantOffset");
            map.MapMember(value => value.AttemptCount).SetElementName("attemptCount");
            map.MapMember(value => value.ExceptionType).SetElementName("exceptionType");
            map.MapMember(value => value.FailedAtTicks).SetElementName("failedAtTicks");
            map.MapMember(value => value.ResolvedAtTicks).SetElementName("resolvedAtTicks").SetIgnoreIfNull(true);
            map.MapMember(value => value.WasSkipped).SetElementName("wasSkipped");
        });

    private static void RegisterProjectionLease() => Register<ProjectionLeaseDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.LeaseName).SetElementName("leaseName");
            map.MapMember(value => value.OwnerId).SetElementName("ownerId");
            map.MapMember(value => value.FencingToken).SetElementName("fencingToken");
            map.MapMember(value => value.LeaseUntilTicks).SetElementName("leaseUntilTicks");
        });

    private static void RegisterOutbox() => Register<OutboxDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.MessageId)
                .SetElementName("messageId")
                .SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.StreamId).SetElementName("streamId");
            map.MapMember(value => value.AggregateType).SetElementName("aggregateType");
            map.MapMember(value => value.StreamVersion).SetElementName("streamVersion");
            map.MapMember(value => value.TenantOffset).SetElementName("tenantOffset");
            map.MapMember(value => value.EventType).SetElementName("eventType");
            map.MapMember(value => value.EventTypeVersion).SetElementName("eventTypeVersion");
            map.MapMember(value => value.Payload).SetElementName("payload");
            map.MapMember(value => value.OccurredAtTicks).SetElementName("occurredAtTicks");
            map.MapMember(value => value.CorrelationId).SetElementName("correlationId").SetIgnoreIfNull(true);
            map.MapMember(value => value.CausationId).SetElementName("causationId").SetIgnoreIfNull(true);
            map.MapMember(value => value.Actor).SetElementName("actor").SetIgnoreIfNull(true);
            map.MapMember(value => value.Headers).SetElementName("headers").SetIgnoreIfNull(true);
            map.MapMember(value => value.AttemptCount).SetElementName("attemptCount");
            map.MapMember(value => value.PublishedAtTicks).SetElementName("publishedAtTicks").SetIgnoreIfNull(true);
        });

    private static void RegisterOutboxAttempt() => Register<OutboxAttemptDocument>(
        map =>
        {
            map.AutoMap();
            map.MapIdMember(value => value.Id);
            map.MapMember(value => value.MessageId)
                .SetElementName("messageId")
                .SetSerializer(new GuidSerializer(GuidRepresentation.Standard));
            map.MapMember(value => value.TenantId).SetElementName("tenantId");
            map.MapMember(value => value.AttemptNumber).SetElementName("attemptNumber");
            map.MapMember(value => value.AttemptedAtTicks).SetElementName("attemptedAtTicks");
            map.MapMember(value => value.Succeeded).SetElementName("succeeded");
            map.MapMember(value => value.ExceptionType).SetElementName("exceptionType").SetIgnoreIfNull(true);
        });

    private static void Register<TDocument>(Action<BsonClassMap<TDocument>> configure)
    {
        if (BsonClassMap.IsClassMapRegistered(typeof(TDocument)))
        {
            return;
        }

        BsonClassMap.RegisterClassMap<TDocument>(map =>
        {
            map.SetIgnoreExtraElements(true);
            configure(map);
        });
    }
}
