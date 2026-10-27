using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Captures both formatted log messages and the structured state properties so
/// tests can assert that secrets never reach logs in any representation.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();
    private readonly ConcurrentQueue<string> _structuredValues = new();

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public IReadOnlyCollection<string> StructuredValues => _structuredValues.ToArray();

    /// <summary>Formatted messages and structured values combined.</summary>
    public IReadOnlyCollection<string> AllText => [.. _messages, .. _structuredValues];

    public ILogger CreateLogger(string categoryName) =>
        new RecordingLogger(_messages, _structuredValues);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(
        ConcurrentQueue<string> messages,
        ConcurrentQueue<string> structuredValues) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue(formatter(state, exception));

            if (state is IReadOnlyList<KeyValuePair<string, object?>> properties)
            {
                foreach (var (_, value) in properties)
                {
                    if (value is not null)
                    {
                        structuredValues.Enqueue(
                            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    }
                }
            }

            if (exception is not null)
            {
                structuredValues.Enqueue(exception.ToString());
            }
        }
    }
}
