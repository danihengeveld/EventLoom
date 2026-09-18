using EventLoom;
using EventLoom.AspNetCore;
using EventLoom.EntityFrameworkCore;
using EventLoom.EntityFrameworkCore.Sqlite;
using EventLoom.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddEventLoom()
    .UseSqlite(builder.Configuration.GetConnectionString("EventStore") ?? "Data Source=eventloom.db")
    .AddEvent<CounterIncremented>()
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

[EventType("counter.incremented")]
public sealed record CounterIncremented : IDomainEvent;

public sealed class Counter(Guid id) : Aggregate<Guid>(id)
{
    public int Value { get; private set; }

    public void Increment() => Raise(new CounterIncremented());

    private void Apply(CounterIncremented _) => Value++;
}
