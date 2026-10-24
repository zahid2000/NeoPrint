using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using NeoPrint.Crypto.Privacy;

namespace NeoPrint.Kiosk.Core.Logging;

/// <summary>
/// The kiosk's and the agent's own log files (docs/KIOSK_UPDATES.md section 6,
/// T-321): one JSON line per entry, one file per day
/// (<c>kiosk-20261025.log</c>, <c>agent-20261025.log</c>), kept seven days and
/// at most 20 MB a day - what <c>UploadLogs</c> sends.
/// </summary>
/// <remarks>
/// Every line is cleaned as it is written (<see cref="LogRedactor"/>): a token,
/// a phone number or a personal QR never reaches the disk. A log that cannot
/// be written is dropped, never an error of its own.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _keepDays;
    private readonly long _maxBytesPerDay;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();

    private DateOnly _day;
    private long _written;

    public FileLoggerProvider(string directory, string prefix, int keepDays = 7, long maxBytesPerDay = 20L * 1024 * 1024, Func<DateTime>? now = null)
    {
        _directory = directory;
        _prefix = prefix;
        _keepDays = keepDays;
        _maxBytesPerDay = maxBytesPerDay;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Lowest level written.</summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var now = _now();
        var line = JsonSerializer.Serialize(new
        {
            t = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            l = level.ToString(),
            c = category,
            m = LogRedactor.Redact(message),
            e = exception is null ? null : LogRedactor.Redact($"{exception.GetType().Name}: {exception.Message}"),
        }) + "\n";

        lock (_gate)
        {
            try
            {
                var day = DateOnly.FromDateTime(now);
                var path = Path.Combine(_directory, $"{_prefix}-{day:yyyyMMdd}.log");
                if (day != _day)
                {
                    _day = day;
                    _written = File.Exists(path) ? new FileInfo(path).Length : 0;
                    Directory.CreateDirectory(_directory);
                    DeleteOld(day);
                }

                var bytes = Encoding.UTF8.GetByteCount(line);
                if (_written + bytes > _maxBytesPerDay)
                {
                    return;
                }

                File.AppendAllText(path, line, Encoding.UTF8);
                _written += bytes;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // A full disk or a missing folder must not take the kiosk down with it.
            }
        }
    }

    private void DeleteOld(DateOnly today)
    {
        foreach (var file in Directory.GetFiles(_directory, $"{_prefix}-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)[(_prefix.Length + 1)..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", out var day) && day < today.AddDays(-_keepDays))
            {
                File.Delete(file);
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
