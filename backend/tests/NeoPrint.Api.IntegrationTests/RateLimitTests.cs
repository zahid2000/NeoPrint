using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The per-IP limits (plan section 6: auth endpoints 10 per minute), with the
/// limiter switched back on - the shared fixture turns it off so the suite is
/// not throttled by itself.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RateLimitTests
{
    private readonly NeoPrintApiFixture _fixture;

    public RateLimitTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task Guessing_a_password_is_stopped_but_refreshing_a_session_is_not()
    {
        await using var limited = _fixture.WithWebHostBuilder(builder => builder
            .UseSetting("RateLimits:Disabled", "false")
            .UseSetting("RateLimits:AuthPerMinute", "10")
            .UseSetting("RateLimits:RefreshPerMinute", "60"));

        using var client = limited.CreateClient();

        // Eleven wrong passwords from one address: the eleventh is refused.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            var login = await client.PostAsJsonAsync(
                "/api/auth/admin/login", new { email = "nobody@neoprint.test", password = "wrong-password" });
            statuses.Add(login.StatusCode);
        }

        statuses.Take(10).Should().NotContain(HttpStatusCode.TooManyRequests);
        statuses.Last().Should().Be(HttpStatusCode.TooManyRequests);

        // Fifteen refreshes from the same address - a panel reloaded again
        // and again, or phones behind one NAT - are answered, not throttled.
        for (var i = 0; i < 15; i++)
        {
            var refresh = await client.PostAsync("/api/auth/admin/refresh", null);
            refresh.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, $"refresh {i + 1}");
        }
    }
}
