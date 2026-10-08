using EventLoom;
using EventLoom.AspNetCore;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;
using EventLoom.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddEventLoom()
    .UseSqlite(builder.Configuration.GetConnectionString("EventStore") ?? "Data Source=eventloom.db")
    .AddAggregate<Counter, Guid>(aggregate => aggregate
        .ConstructWith(id => new Counter(id))
        .UseStream("counter", id => id.ToString("D")));
builder.Services.AddEventLoomHealthChecks();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    await app.InitializeEventLoomDevelopmentDatabaseAsync();
}

app.MapEventLoomHealthChecks();
app.MapPost("/counters/{id:guid}/increment", async (
    Guid id,
    AggregateRepository<Counter, Guid> repository) =>
{
    var counter = await repository.LoadAsync(id);
    counter.Increment();
    await repository.SaveAsync(counter);
    return Results.Ok(new { counter.Id, counter.Value });
});
app.Run();

public sealed record CounterIncremented : IDomainEvent<CounterIncremented, Counter>
{
    public static string EventType => "counter.incremented";
}

public sealed class Counter(Guid id) : Aggregate<Counter, Guid>(id), IApply<CounterIncremented>
{
    public int Value { get; private set; }

    public void Increment() => Raise(new CounterIncremented());

    void IApply<CounterIncremented>.Apply(CounterIncremented _) => Value++;
}
