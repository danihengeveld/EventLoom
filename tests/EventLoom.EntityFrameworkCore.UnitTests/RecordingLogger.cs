using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EventLoom.EntityFrameworkCore.UnitTests;

internal sealed record RecordedLog(
    LogLevel Level,
    int EventId,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    Exception? Exception);

internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<RecordedLog> entries = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<RecordedLog>> completions = new();

    public IReadOnlyList<RecordedLog> Entries => entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(value => value.Key, value => value.Value)
            : new Dictionary<string, object?>();
        var entry = new RecordedLog(logLevel, eventId.Id, formatter(state, exception), properties, exception);
        entries.Enqueue(entry);
        if (completions.TryGetValue(eventId.Id, out var completion))
        {
            completion.TrySetResult(entry);
        }
    }

    public Task<RecordedLog> WaitForAsync(int eventId)
    {
        var completion = completions.GetOrAdd(eventId, _ =>
            new TaskCompletionSource<RecordedLog>(TaskCreationOptions.RunContinuationsAsynchronously));
        var existing = Entries.FirstOrDefault(entry => entry.EventId == eventId);
        if (existing is not null)
        {
            completion.TrySetResult(existing);
        }

        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
