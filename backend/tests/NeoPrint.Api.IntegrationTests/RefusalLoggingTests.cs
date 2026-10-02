using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A refused rule over HTTP is a warning in the log, not an error
/// (<c>docs/OBSERVABILITY.md</c>): errors are what an alert should fire on.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RefusalLoggingTests
{
    private readonly NeoPrintApiFixture _fixture;

    public RefusalLoggingTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task A_refused_request_is_logged_as_a_warning_and_never_as_an_error()
    {
        var captured = new CapturingProvider();
        await using var host = _fixture.WithHttpOnlyHost(builder => builder.ConfigureLogging(logging => logging.AddProvider(captured)));
        using var client = host.CreateClient();

        // A device the server has never seen asks to sign in: a rule says no.
        var refused = await client.PostAsJsonAsync("/api/devices/challenge", new { deviceId = Guid.NewGuid() });

        ((int)refused.StatusCode).Should().BeInRange(400, 499);
        captured.Entries.Should().NotContain(
            entry => entry.Level >= LogLevel.Error,
            "a refusal is the rule working; ASP.NET's exception middleware and the request pipeline used to log it as 'fail'");
        captured.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Rejected", StringComparison.Ordinal));
    }

    private sealed record Entry(string Category, LogLevel Level, string Message);

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentBag<Entry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentBag<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Add(new Entry(category, logLevel, formatter(state, exception)));
        }
    }
}
