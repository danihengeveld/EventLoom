namespace EventLoom.EntityFrameworkCore;

public sealed class EventStoreOptions
{
    public string Schema { get; set; } = "eventloom";

    public string TablePrefix { get; set; } = "eventloom_";

    public bool UseSchema { get; set; }
}
