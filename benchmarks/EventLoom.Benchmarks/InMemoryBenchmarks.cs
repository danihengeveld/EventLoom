using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using EventLoom;

namespace EventLoom.Benchmarks;

[MemoryDiagnoser]
public class SerializationBenchmarks
{
    private EventSerializer serializer = null!;
    private CounterIncremented @event = null!;
    private string payload = null!;

    [Params(32, 4096)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        serializer = new EventSerializer(new EventRegistry().RegisterEvent<CounterIncremented>());
        @event = new CounterIncremented(1, new string('x', PayloadBytes));
        payload = serializer.Serialize(@event);
    }

    [Benchmark]
    public string Serialize() => serializer.Serialize(@event);

    [Benchmark]
    public object Deserialize() => serializer.Deserialize("benchmarks.counter-incremented", 1, payload);
}

[MemoryDiagnoser]
public class UpcastingBenchmarks
{
    private EventSerializer serializer = null!;
    private string payload = null!;
    private string currentPayload = null!;

    [GlobalSetup]
    public void Setup()
    {
        var registry = new EventRegistry()
            .RegisterEvent<CounterIncrementedV1>()
            .RegisterEvent<CounterIncrementedV2>()
            .RegisterEvent<CounterIncrementedV3>();
        serializer = new EventSerializer(registry, [new AddDataUpcaster(), new AddAmountUpcaster()]);
        payload = """{"amount":1}""";
        currentPayload = serializer.Serialize(new CounterIncrementedV3(1, "benchmark", 0));
    }

    [Benchmark(Baseline = true)]
    public object DeserializeCurrentVersion() =>
        serializer.Deserialize("benchmarks.upcast-counter", 3, currentPayload);

    [Benchmark]
    public object DeserializeTwoVersionUpcasts() =>
        serializer.Deserialize("benchmarks.upcast-counter", 1, payload);

    [EventType("benchmarks.upcast-counter", Version = 1)]
    public sealed record CounterIncrementedV1(int Amount) : IDomainEvent<BenchmarkCounter>;

    [EventType("benchmarks.upcast-counter", Version = 2)]
    public sealed record CounterIncrementedV2(int Amount, string Data) : IDomainEvent<BenchmarkCounter>;

    [EventType("benchmarks.upcast-counter", Version = 3)]
    public sealed record CounterIncrementedV3(int Amount, string Data, int Extra) : IDomainEvent<BenchmarkCounter>;

    private sealed class AddDataUpcaster : IEventUpcaster
    {
        public string EventName => "benchmarks.upcast-counter";
        public int FromVersion => 1;
        public int ToVersion => 2;

        public JsonElement Upcast(JsonElement payload)
        {
            var node = JsonNode.Parse(payload.GetRawText())!.AsObject();
            node["data"] = "benchmark";
            return JsonSerializer.SerializeToElement(node);
        }
    }

    private sealed class AddAmountUpcaster : IEventUpcaster
    {
        public string EventName => "benchmarks.upcast-counter";
        public int FromVersion => 2;
        public int ToVersion => 3;

        public JsonElement Upcast(JsonElement payload)
        {
            var node = JsonNode.Parse(payload.GetRawText())!.AsObject();
            node["extra"] = 0;
            return JsonSerializer.SerializeToElement(node);
        }
    }
}

[MemoryDiagnoser]
public class ReplayBenchmarks
{
    private object[] events = null!;

    [Params(10, 100, 1000)]
    public int EventCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        events = Enumerable.Range(0, EventCount)
            .Select(_ => (object)new CounterIncremented(1, "benchmark"))
            .ToArray();
        new BenchmarkCounter(Guid.Empty).ReplayEvents(events);
    }

    [Benchmark]
    public BenchmarkCounter Replay()
    {
        var counter = new BenchmarkCounter(Guid.Empty);
        counter.ReplayEvents(events);
        return counter;
    }
}
