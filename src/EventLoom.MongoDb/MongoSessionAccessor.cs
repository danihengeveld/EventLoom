using MongoDB.Driver;

namespace EventLoom.MongoDb;

/// <summary>Exposes the current EventLoom MongoDB append session to inline projection handlers.</summary>
public sealed class MongoSessionAccessor
{
    private readonly IMongoDatabase database;
    private IClientSessionHandle? session;

    internal MongoSessionAccessor(MongoStorageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        database = catalog.Database;
    }

    /// <summary>Gets the EventLoom MongoDB database.</summary>
    public IMongoDatabase Database => database;

    /// <summary>
    /// Gets the current EventLoom append session when code runs inside an inline projection; otherwise <see langword="null"/>.
    /// </summary>
    public IClientSessionHandle? Session => session;

    internal IDisposable Enter(IClientSessionHandle value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var previous = session;
        session = value;
        return new Scope(this, previous);
    }

    private sealed class Scope(MongoSessionAccessor accessor, IClientSessionHandle? previous) : IDisposable
    {
        private readonly MongoSessionAccessor accessor = accessor;
        private readonly IClientSessionHandle? previous = previous;
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            accessor.session = previous;
        }
    }
}
