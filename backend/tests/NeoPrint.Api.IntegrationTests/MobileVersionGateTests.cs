using System.Text.Json;

using Microsoft.AspNetCore.Http;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Clients;
using NeoPrint.Crypto.Releases;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The oldest client the backend supports (ADR-0009, T-492): a mobile app
/// older than that is told to update, the kiosk and the panel are never refused.
/// </summary>
public sealed class MobileVersionGateTests
{
    private readonly FakeSettings _settings = new();
    private readonly FakeClock _clock = new();

    [Fact]
    public void The_minimums_are_the_contract_files()
    {
        var contract = ClientApiContract.LoadContract();

        SupportedClients.Kiosk.Should().Be(ReleaseVersion.Parse(contract["minSupported"]!["kiosk"]!.GetValue<string>()));
        SupportedClients.Mobile.Should().Be(ReleaseVersion.Parse(contract["minSupported"]!["mobile"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("0.9.0-m20.14", "0.9.0-m20.2", true)]
    [InlineData("0.9.0-m20.2", "0.9.0-m20.2", true)]
    [InlineData("0.9.0-m19b", "0.9.0-m20.2", false)]
    [InlineData("0.8.0", "0.9.0-m20.2", false)]
    [InlineData("1.0.0", "0.9.0-m20.2", true)]
    [InlineData(null, "0.9.0-m20.2", null)]
    [InlineData("dev", "0.9.0-m20.2", null)]
    public void A_version_is_supported_from_the_minimum_on(string? version, string minimum, bool? supported)
        => SupportedClients.IsSupported(version, ReleaseVersion.Parse(minimum)).Should().Be(supported);

    [Fact]
    public async Task An_app_older_than_the_api_supports_is_told_to_update()
    {
        _settings.Values[SettingKeys.MobileMinVersion] = "2.0.0";

        var (status, body) = await SendAsync("1.9.9");

        status.Should().Be(StatusCodes.Status426UpgradeRequired);
        body!.Value.GetProperty("code").GetString().Should().Be("app.updateRequired");
        body.Value.GetProperty("minVersion").GetString().Should().Be("2.0.0");
        body.Value.GetProperty("detail").GetString().Should().Contain("2.0.0");
    }

    [Fact]
    public async Task An_app_as_new_as_the_minimum_goes_through()
    {
        _settings.Values[SettingKeys.MobileMinVersion] = "2.0.0";

        (await SendAsync("2.0.0")).Status.Should().Be(StatusCodes.Status200OK);
        (await SendAsync("2.1.0")).Status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task A_request_that_does_not_say_its_version_is_never_refused()
    {
        // The kiosk and the panel: a kiosk's update comes through the API too.
        _settings.Values[SettingKeys.MobileMinVersion] = "99.0.0";

        (await SendAsync(version: null)).Status.Should().Be(StatusCodes.Status200OK);
        (await SendAsync("not-a-version")).Status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task The_panel_cannot_let_in_an_app_older_than_the_api_serves()
    {
        _settings.Values[SettingKeys.MobileMinVersion] = "0.0.1";

        // Older than the API's own minimum, newer than the panel's: still refused.
        (await SendAsync("0.0.1")).Status.Should().Be(StatusCodes.Status426UpgradeRequired);
        (await SendAsync(SupportedClients.Mobile!.ToString())).Status.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task A_changed_minimum_is_read_again_after_a_minute()
    {
        var gate = Gate();
        (await SendAsync("1.0.0", gate)).Status.Should().Be(StatusCodes.Status200OK);

        _settings.Values[SettingKeys.MobileMinVersion] = "2.0.0";
        (await SendAsync("1.0.0", gate)).Status.Should().Be(StatusCodes.Status200OK, "remembered for a minute");

        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
        (await SendAsync("1.0.0", gate)).Status.Should().Be(StatusCodes.Status426UpgradeRequired);
    }

    private MobileVersionGate Gate() => new(
        context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        },
        _clock);

    private async Task<(int Status, JsonElement? Body)> SendAsync(string? version, MobileVersionGate? gate = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/wallet";
        context.Response.Body = new MemoryStream();
        if (version is not null)
        {
            context.Request.Headers[SupportedClients.AppVersionHeader] = version;
        }

        await (gate ?? Gate()).InvokeAsync(context, _settings);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var text = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, text.Length == 0 ? null : JsonDocument.Parse(text).RootElement.Clone());
    }

    private sealed class FakeSettings : ISettingsReader
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task<int> GetIntAsync(string key, int fallback, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.TryGetValue(key, out var value) && int.TryParse(value, out var number) ? number : fallback);

        public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.TryGetValue(key, out var value) && bool.TryParse(value, out var flag) ? flag : fallback);
    }

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);
    }
}
