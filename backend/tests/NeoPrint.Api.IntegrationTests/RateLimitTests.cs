using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MongoDB.Driver;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Domain.Admin;
using NeoPrint.Infrastructure.Persistence;

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
        await using var limited = _fixture.WithHttpOnlyHost(builder => builder
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

    // Before T-306 the limiter ran ahead of authentication: every request was
    // counted by its address, and behind the proxy every request has one.
    [DockerFact]
    public async Task Each_signed_in_caller_has_its_own_limit_and_a_refused_one_is_told_when_to_try_again()
    {
        await using var limited = _fixture.WithHttpOnlyHost(builder => builder
            .UseSetting("RateLimits:Disabled", "false")
            .UseSetting("RateLimits:GlobalPerMinute", "5"));

        using var first = limited.CreateClient();
        using var second = limited.CreateClient();
        first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await SignInAsync(first, NeoPrintApiFixture.SeedAdminEmail, NeoPrintApiFixture.SeedAdminPassword));
        second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await AnotherAdminAsync(second));

        for (var i = 0; i < 5; i++)
        {
            (await first.GetAsync("/api/admin/me")).StatusCode.Should().Be(HttpStatusCode.OK, $"request {i + 1}");
        }

        var refused = await first.GetAsync("/api/admin/me");
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.Should().NotBeNull("the caller is told when to try again");
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("problem.tooManyRequests");
        // The framework's limiter knows the window, not what is left of it: no number (T-310).
        problem.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace()
            .And.NotMatchRegex(@"\d", "a whole window is not how long to wait");

        // Another account from the same address is not held up by the first.
        (await second.GetAsync("/api/admin/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task An_anonymous_caller_cannot_choose_the_address_it_is_counted_by()
    {
        // The test server has no address of its own; Kestrel always has one. A caller on the internet:
        await using var limited = _fixture.WithHttpOnlyHost(builder => builder
            .UseSetting("RateLimits:Disabled", "false")
            .UseSetting("RateLimits:GlobalPerMinute", "5")
            .ConfigureServices(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FromTheInternet>()));

        using var client = limited.CreateClient();

        // Not from a known proxy: the header is not believed, all six are one caller.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/map/config");
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        statuses[^1].Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("172.25.0.4", "203.0.113.7")]
    [InlineData("192.168.65.3", "203.0.113.7")]
    [InlineData("8.8.8.8", "8.8.8.8")]
    public async Task Only_a_configured_proxy_may_say_who_the_caller_is(string from, string seen)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks"] = "172.16.0.0/12,192.168.0.0/16" })
            .Build();
        var options = new ForwardedHeadersOptions();
        RequestLimits.ConfigureForwardedHeaders(options, configuration);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(from);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";

        await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options)).Invoke(context);

        context.Connection.RemoteIpAddress!.ToString().Should().Be(seen);
        RequestLimits.CallerKey(context).Should().Be($"ip:{seen}");
    }

    [Fact]
    public void A_misspelt_proxy_network_stops_the_start()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders:KnownNetworks"] = "172.16.0.0" })
            .Build();

        FluentActions.Invoking(() => RequestLimits.Networks(configuration)).Should().Throw<InvalidOperationException>();
    }

    // -----------------------------------------------------------------------
    // A kiosk's own calls behind one router (T-309)
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task Kiosks_behind_one_router_register_with_their_own_enrollment_tokens_and_sign_in_without_holding_each_other_up()
    {
        await using var limited = _fixture.WithHttpOnlyHost(builder => builder
            .UseSetting("RateLimits:Disabled", "false")
            .UseSetting("RateLimits:DeviceRegisterPerHour", "1")
            .UseSetting("RateLimits:AuthPerMinute", "3")
            .ConfigureServices(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FromOneRouter>()));

        using var admin = limited.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await SignInAsync(admin, NeoPrintApiFixture.SeedAdminEmail, NeoPrintApiFixture.SeedAdminPassword));
        using var kiosk = limited.CreateClient();

        // Three kiosks of one place, one after another, each with its token: none is refused,
        // though one registration an hour is all one address has.
        var devices = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var token = await admin.PostAsJsonAsync("/api/admin/enrollment-tokens", new
            {
                newKiosk = new { name = $"Router kiosk {Guid.NewGuid():N}"[..30], lat = 40.38, lng = 49.85, address = "Bakı", defaultLanguage = "az", paperCapacityA4 = 500, paperCapacityA3 = 250, adminPin = "135790" },
                ttlHours = 1,
            });
            token.StatusCode.Should().Be(HttpStatusCode.OK, await token.Content.ReadAsStringAsync());
            var registered = await RegisterAsync(kiosk, (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
            registered.StatusCode.Should().Be(HttpStatusCode.OK, $"kiosk {i + 1}: {await registered.Content.ReadAsStringAsync()}");
            devices.Add((await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deviceId").GetGuid());
        }

        // Without a token the address counts: the second in the hour is refused, told to come back later.
        (await RegisterAsync(kiosk, enrollmentToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var refused = await RegisterAsync(kiosk, enrollmentToken: null);
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 3600, "what is left of the hour");
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().Should().NotMatchRegex(@"\d", "an hour is not said in seconds");

        // One kiosk asking too often is stopped, and told exactly when it may again; its neighbour is not.
        for (var i = 0; i < 3; i++)
        {
            (await kiosk.PostAsJsonAsync("/api/devices/challenge", new { deviceId = devices[0] })).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var stopped = await kiosk.PostAsJsonAsync("/api/devices/challenge", new { deviceId = devices[0] });
        stopped.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await stopped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().Should().MatchRegex(@"\d", "what is left of a minute is said");
        (await kiosk.PostAsJsonAsync("/api/devices/challenge", new { deviceId = devices[1] })).StatusCode.Should().Be(HttpStatusCode.OK);

        // The counts live in Redis: a new instance of the API - a restart - still knows them.
        await using var restarted = _fixture.WithHttpOnlyHost(builder => builder
            .UseSetting("RateLimits:Disabled", "false")
            .UseSetting("RateLimits:DeviceRegisterPerHour", "1")
            .UseSetting("RateLimits:AuthPerMinute", "3")
            .ConfigureServices(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FromOneRouter>()));
        using var again = restarted.CreateClient();
        (await RegisterAsync(again, enrollmentToken: null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "a restart does not reset the limit");
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string? enrollmentToken)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register")
        {
            Content = JsonContent.Create(new
            {
                publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                fingerprint = new { hostname = "router-kiosk", os = "Linux", osVersion = "6.1", machineId = Guid.NewGuid().ToString("n"), macAddresses = new[] { "aa:bb:cc:dd:ee:01" }, cpu = "Test", ramGb = 8, screenResolution = "1920x1080" },
                platform = "Linux",
                appVersion = "0.3.0",
                agentVersion = "0.3.0",
                enrollmentToken,
                printers = Array.Empty<object>(),
            }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return client.SendAsync(request);
    }

    /// <summary>Every request from one public address - a place's router - with an address of its own each run.</summary>
    private sealed class FromOneRouter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        private static readonly IPAddress Router = IPAddress.Parse($"198.51.100.{Random.Shared.Next(1, 250)}");

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = Router;
                return following(context);
            });
            next(app);
        };
    }

    private static async Task<string> SignInAsync(HttpClient client, string email, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        return (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private async Task<string> AnotherAdminAsync(HttpClient client)
    {
        const string password = "LimitPassword!2026";
        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<AdminUser>>();
        var role = await context.AdminRoles.Find(r => r.Name == "Admin").SingleAsync();
        var admin = new AdminUser
        {
            Email = $"limit.{Guid.NewGuid():N}@neoprint.test",
            FullName = "Limit Admin",
            RoleIds = [role.Id],
            KioskScope = KioskScope.Everything(),
            IsActive = true,
        };
        admin.PasswordHash = hasher.HashPassword(admin, password);
        await context.AdminUsers.InsertOneAsync(admin);

        return await SignInAsync(client, admin.Email, password);
    }

    /// <summary>Gives every request the address of a caller on the internet, ahead of everything else.</summary>
    private sealed class FromTheInternet : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("8.8.8.8");
                return following(context);
            });
            next(app);
        };
    }
}
