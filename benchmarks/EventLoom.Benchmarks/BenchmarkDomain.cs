namespace EventLoom.Benchmarks;

public sealed record CounterIncremented(int Amount, string Data) : IDomainEvent<CounterIncremented, BenchmarkCounter>
{
    public static string EventType => "benchmarks.counter-incremented";
}

public sealed class BenchmarkCounter(Guid id) : Aggregate<BenchmarkCounter, Guid>(id), IApply<CounterIncremented>, ISnapshotable<CounterSnapshot>
{
    public int Value { get; private set; }

    public void Increment(string data) => Raise(new CounterIncremented(1, data));

    public void ReplayEvents(IEnumerable<object> events) => Replay(events);

    void IApply<CounterIncremented>.Apply(CounterIncremented @event) => Value += @event.Amount;

    CounterSnapshot ISnapshotable<CounterSnapshot>.CreateSnapshot() => new(Value);

    void ISnapshotable<CounterSnapshot>.RestoreSnapshot(CounterSnapshot snapshot) => Value = snapshot.Value;
}

public sealed record CounterSnapshot(int Value) : IAggregateSnapshot<CounterSnapshot, BenchmarkCounter>
{
    public static string SnapshotType => "benchmarks.counter";
}
