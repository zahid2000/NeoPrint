using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Sentry;

using NeoPrint.Api.Infrastructure;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Error tracking (T-478): each client gets its own DSN, none when it is not
/// configured or not an address, and what the API sends carries no caller.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ErrorTrackingTests
{
    private const string MobileDsn = "https://0123456789abcdef0123456789abcdef@errors.example.test/3";

    private readonly NeoPrintApiFixture _fixture;

    public ErrorTrackingTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task A_client_gets_its_own_dsn_and_none_when_it_is_missing_or_not_an_address()
    {
        if (_fixture.Skipped)
        {
            return;
        }

        await using var host = _fixture.WithHttpOnlyHost(builder => builder
            .UseSetting("ErrorTracking:MobileDsn", MobileDsn)
            .UseSetting("ErrorTracking:AdminDsn", "not a dsn")
            .UseSetting("ErrorTracking:KioskDsn", "https://errors.example.test/4"));
        using var client = host.CreateClient();

        // The API leaves null properties out of its JSON.
        async Task<string?> DsnOf(string name)
        {
            var response = await client.GetAsync($"/api/error-tracking/{name}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            return body.TryGetProperty("dsn", out var dsn) ? dsn.GetString() : null;
        }

        (await DsnOf("mobile")).Should().Be(MobileDsn, "no sign-in is needed to report a crash");
        (await DsnOf("admin")).Should().BeNull("a value that is not an address turns reporting off");
        (await DsnOf("kiosk")).Should().BeNull("an address with no key is not a DSN");

        (await client.GetAsync("/api/error-tracking/api")).StatusCode.Should().Be(
            HttpStatusCode.NotFound, "the API's own DSN is not handed out");
    }

    [Fact]
    public void What_the_api_reports_names_no_caller_and_carries_no_credential()
    {
        var sentryEvent = new SentryEvent(new InvalidOperationException("boom"))
        {
            User = new SentryUser { Email = "someone@example.test", IpAddress = "203.0.113.7" },
        };
        sentryEvent.Request.Headers["Authorization"] = "Bearer secret";
        sentryEvent.Request.Cookies = "refresh=secret";
        sentryEvent.Request.QueryString = "token=secret";
        sentryEvent.Request.Data = "{\"password\":\"secret\"}";

        var sent = ErrorTracking.Scrub(sentryEvent, new SentryHint());

        sent.User.Email.Should().BeNull();
        sent.User.IpAddress.Should().BeNull();
        sent.Request.Headers.Should().BeEmpty();
        sent.Request.Cookies.Should().BeNull();
        sent.Request.QueryString.Should().BeNull();
        sent.Request.Data.Should().BeNull();
        sent.Exception.Should().NotBeNull("the error itself is what is reported");
    }
}
