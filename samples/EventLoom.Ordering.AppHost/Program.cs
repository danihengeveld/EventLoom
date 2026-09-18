var builder = DistributedApplication.CreateBuilder(args);

var eventStore = builder.AddPostgres("postgres")
    .AddDatabase("eventstore");

builder.AddProject<Projects.EventLoom_Ordering_Api>("ordering-api", "API (direct)")
    .WithReference(eventStore)
    .WaitFor(eventStore)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
