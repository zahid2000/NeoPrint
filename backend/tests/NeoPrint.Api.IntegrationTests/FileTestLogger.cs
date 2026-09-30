using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Writes the API's warnings and errors to the file named by
/// <c>NEOPRINT_IT_LOG</c>, for finding out why a test failed in the full run
/// when it passes on its own. Off unless the variable is set.
/// </summary>
internal sealed class FileTestLoggerProvider : ILoggerProvider
{
    public const string Variable = "NEOPRINT_IT_LOG";

    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<string, FileTestLogger> _loggers = new();

    public FileTestLoggerProvider(string path)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileTestLogger(name, _writer));

    public void Dispose() => _writer.Dispose();

    private sealed class FileTestLogger(string category, StreamWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            lock (writer)
            {
                writer.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {category}: {formatter(state, exception)}");

                if (exception is not null)
                {
                    writer.WriteLine(exception.ToString());
                }
            }
        }
    }
}
