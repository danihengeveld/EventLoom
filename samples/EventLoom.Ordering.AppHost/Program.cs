#pragma warning disable ASPIREMONGODB001 // WithReplicaSet is an experimental Aspire API.
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

// Select the storage provider with `EventLoom:Provider=MongoDb` (or --EventLoom:Provider MongoDb);
// PostgreSQL is the default.
var provider = builder.Configuration["EventLoom:Provider"] ?? "PostgreSql";

var api = builder.AddProject<EventLoom_Ordering_Api>("ordering-api", "API (direct)")
    .WithEnvironment("EventLoom__Provider", provider)
    .WithHttpHealthCheck("/health");

if (string.Equals(provider, "MongoDb", StringComparison.OrdinalIgnoreCase))
{
    // EventLoom's MongoDB provider requires transactions, so the server runs as a single-node replica set.
    var eventStore = builder.AddMongoDB("mongo")
        .WithReplicaSet()
        .AddDatabase("eventstore");
    api.WithReference(eventStore).WaitFor(eventStore);
}
else
{
    var eventStore = builder.AddPostgres("postgres")
        .AddDatabase("eventstore");
    api.WithReference(eventStore).WaitFor(eventStore);
}

builder.Build().Run();
