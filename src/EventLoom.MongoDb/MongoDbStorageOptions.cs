namespace EventLoom.MongoDb;

/// <summary>Configures MongoDB collection naming and transaction behavior for EventLoom storage.</summary>
public sealed class MongoDbStorageOptions
{
    /// <summary>Gets or sets the prefix applied to every EventLoom collection. The default is <c>eventloom_</c>.</summary>
    public string CollectionPrefix { get; set; } = "eventloom_";

    /// <summary>
    /// Gets or sets the maximum time MongoDB may spend committing one EventLoom transaction.
    /// The default is 30 seconds.
    /// </summary>
    public TimeSpan TransactionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether EventLoom registers the driver's <c>GuidSerializer</c> with
    /// <c>GuidRepresentation.Standard</c> process-wide so application documents can contain <see cref="Guid"/> values
    /// in the EventLoom database. The default is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// MongoDB.Driver 3 refuses to serialize a <see cref="Guid"/> until a representation is configured. Registration is
    /// best effort: it is skipped when the application or the driver has already registered a different
    /// <see cref="Guid"/> serializer. Set this to <see langword="false"/> when the application registers its own
    /// <see cref="Guid"/> serializer after calling <c>UseMongoDb</c>. EventLoom's own documents never depend on it.
    /// </remarks>
    public bool RegisterStandardGuidSerializer { get; set; } = true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CollectionPrefix);
        if (TransactionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TransactionTimeout));
        }
    }
}
