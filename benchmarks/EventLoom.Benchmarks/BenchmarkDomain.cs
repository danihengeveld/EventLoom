using EventLoom;

namespace EventLoom.Benchmarks;

[EventType("benchmarks.counter-incremented", Version = 1)]
public sealed record CounterIncremented(int Amount, string Data) : IDomainEvent<BenchmarkCounter>;

public sealed class BenchmarkCounter(Guid id) : Aggregate<Guid>(id)
{
    public int Value { get; private set; }

    public void Increment(string data) => Raise(new CounterIncremented(1, data));

    public void ReplayEvents(IEnumerable<object> events) => Replay(events);

    private void Apply(CounterIncremented @event) => Value += @event.Amount;

    private CounterSnapshot CaptureSnapshot() => new(Value);

    private void RestoreSnapshot(CounterSnapshot snapshot) => Value = snapshot.Value;
}

[SnapshotType("benchmarks.counter")]
public sealed record CounterSnapshot(int Value) : IAggregateSnapshot<BenchmarkCounter>;
