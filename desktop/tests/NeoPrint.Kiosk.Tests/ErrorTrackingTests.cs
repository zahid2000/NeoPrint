using Microsoft.Extensions.Logging.Abstractions;

using Sentry;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk's error tracking (T-478): started only with the DSN the API hands
/// out, never in the start's way, and what it sends names no machine or user.
/// </summary>
public sealed class ErrorTrackingTests
{
    [Fact]
    public async Task It_starts_only_with_a_dsn_and_a_failing_api_does_not_stop_the_kiosk()
    {
        var started = new List<SentryOptions>();
        void Init(SentryOptions options) => started.Add(options);

        Task<ErrorTracking.Config?> Answer(ErrorTracking.Config? config) => Task.FromResult(config);

        (await ErrorTracking.StartAsync("https://api.example.test", NullLogger.Instance, (_, _) => Answer(new(null, "Production")), Init))
            .Should().BeFalse();
        (await ErrorTracking.StartAsync("https://api.example.test", NullLogger.Instance, (_, _) => throw new HttpRequestException("offline"), Init))
            .Should().BeFalse("no network is not a reason to stop");
        started.Should().BeEmpty();

        Uri? asked = null;
        (await ErrorTracking.StartAsync(
                "https://api.example.test/",
                NullLogger.Instance,
                (address, _) =>
                {
                    asked = address;
                    return Answer(new("https://key@errors.example.test/2", "Production"));
                },
                Init))
            .Should().BeTrue();

        asked.Should().Be(new Uri("https://api.example.test/api/error-tracking/kiosk"));
        var options = started.Should().ContainSingle().Subject;
        options.Dsn.Should().Be("https://key@errors.example.test/2");
        options.SendDefaultPii.Should().BeFalse();
        options.MaxBreadcrumbs.Should().Be(0, "what the customer did stays on the kiosk");
        options.ServerName.Should().BeEmpty();
    }

    [Fact]
    public void What_is_sent_names_no_machine_and_no_user()
    {
        var sentryEvent = new SentryEvent(new InvalidOperationException("boom"))
        {
            ServerName = "KIOSK-PC-01",
            User = new SentryUser { Username = "kiosk" },
        };

        var sent = ErrorTracking.Scrub(sentryEvent, new SentryHint());

        sent.ServerName.Should().BeNull();
        sent.User.Username.Should().BeNull();
        sent.Exception.Should().NotBeNull();
    }
}
