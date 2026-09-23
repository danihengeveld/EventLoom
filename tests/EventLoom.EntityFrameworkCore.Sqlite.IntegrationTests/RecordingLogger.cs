using Microsoft.Extensions.Logging;

namespace EventLoom.EntityFrameworkCore.Sqlite.IntegrationTests;

internal sealed record RecordedLog(
    LogLevel Level, int EventId, string Message, IReadOnlyDictionary<string, object?> Properties, Exception? Exception);

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<RecordedLog> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(value => value.Key, value => value.Value)
            : new Dictionary<string, object?>();
        Entries.Add(new RecordedLog(logLevel, eventId.Id, formatter(state, exception), properties, exception));
    }
}
