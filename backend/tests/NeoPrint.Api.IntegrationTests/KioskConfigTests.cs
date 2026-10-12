using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// What a kiosk fetches once it is activated, and how it reports in
/// (plan sections 7.9, 7.11, 6).
/// </summary>
/// <remarks>
/// The kiosk application is a universal build, so everything that makes it this
/// kiosk comes from the configuration endpoint. These use the same requests the
/// application makes, signing the challenge with a key pair generated here.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class KioskConfigTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KioskConfigTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    /// <summary>A device with its own key pair, as the kiosk application has.</summary>
    private sealed class FakeDevice : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        public string Sign(string nonce)
            => Convert.ToBase64String(_key.SignData(
                System.Text.Encoding.UTF8.GetBytes(nonce),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public object Fingerprint(string hostname) => new
        {
            hostname,
            os = "Linux",
            osVersion = "6.1",
            machineId = Guid.NewGuid().ToString("n"),
            macAddresses = new[] { "aa:bb:cc:dd:ee:ff" },
            cpu = "Test CPU",
            ramGb = 8,
            screenResolution = "1920x1080",
        };

        public void Dispose() => _key.Dispose();
    }

    private static object NewKiosk(string name, string? printer = null) => new
    {
        name,
        lat = 40.3793,
        lng = 49.8486,
        address = "Bakı",
        defaultLanguage = "az",
        paperCapacityA4 = 500,
        paperCapacityA3 = 250,
        adminPin = "135790",
    };

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("accessToken").GetString());

        return client;
    }

    /// <summary>Registers a device, activates it, and returns a kiosk client.</summary>
    private async Task<(HttpClient Kiosk, Guid DeviceId, Guid KioskId, string KioskCode)>
        ActivatedKioskAsync(string hostname, string? printerName = null)
    {
        using var anonymous = _fixture.CreateApiClient();
        using var device = new FakeDevice();

        var printers = printerName is null
            ? Array.Empty<object>()
            :
            [
                new
                {
                    name = printerName,
                    driverName = "Test driver",
                    isOnline = true,
                    color = true,
                    duplex = true,
                    a3 = false,
                },
            ];

        var register = await anonymous.PostAsJsonAsync("/api/devices/register", new
        {
            publicKey = device.PublicKey,
            fingerprint = device.Fingerprint(hostname),
            platform = "Linux",
            appVersion = "0.1.0",
            agentVersion = "0.1.0",
            printers,
        });

        register.StatusCode.Should().Be(HttpStatusCode.OK, await register.Content.ReadAsStringAsync());

        var deviceId = (await register.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deviceId").GetGuid();

        using var admin = await SuperAdminAsync();

        var activate = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk($"Config test {hostname}"),
            selectedPrinterName = printerName,
        });

        activate.StatusCode.Should().Be(HttpStatusCode.OK, await activate.Content.ReadAsStringAsync());

        var activated = await activate.Content.ReadFromJsonAsync<JsonElement>();

        // The device signs a challenge for its kiosk token, as the application
        // does on start.
        var challenge = await anonymous.PostAsJsonAsync("/api/devices/challenge", new { deviceId });
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nonce").GetString()!;

        var token = await anonymous.PostAsJsonAsync("/api/devices/token", new
        {
            deviceId,
            nonce,
            signature = device.Sign(nonce),
        });

        var issued = await token.Content.ReadFromJsonAsync<JsonElement>();
        issued.GetProperty("tokenType").GetString().Should().Be("kiosk");

        var kiosk = _fixture.CreateApiClient();
        kiosk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", issued.GetProperty("accessToken").GetString());

        return (
            kiosk,
            deviceId,
            activated.GetProperty("kioskId").GetGuid(),
            activated.GetProperty("kioskCode").GetString()!);
    }

    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task An_activated_kiosk_fetches_its_own_configuration()
    {
        if (Skip) { return; }

        var (kiosk, _, kioskId, kioskCode) = await ActivatedKioskAsync("config-basic", "HP LaserJet");
        using var _client = kiosk;

        var config = await kiosk.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config");

        config.GetProperty("kioskId").GetGuid().Should().Be(kioskId);
        config.GetProperty("code").GetString().Should().Be(kioskCode);
        config.GetProperty("defaultLanguage").GetString().Should().Be("az");
        config.GetProperty("selectedPrinter").GetString().Should().Be("HP LaserJet");

        // Limits fall back to the platform defaults when the kiosk has none
        // (plan section 2.2).
        config.GetProperty("maxFileSizeMb").GetInt32().Should().Be(50);
        config.GetProperty("maxPages").GetInt32().Should().Be(200);
        config.GetProperty("queueDisplayLimit").GetInt32().Should().Be(5);

        // The paper counters the activation set up.
        config.GetProperty("paperStock").GetProperty("A4").GetProperty("capacity").GetInt32()
            .Should().Be(500);

        // And what the kiosk needs to decide it may keep working offline
        // (plan section 7.3).
        config.GetProperty("cacheTtlHours").GetInt32().Should().Be(24);
        config.GetProperty("heartbeatSeconds").GetInt32().Should().Be(15);
    }

    [DockerFact]
    public async Task The_configuration_carries_the_admin_pin_only_as_a_hash()
    {
        if (Skip) { return; }

        var (kiosk, _, _, _) = await ActivatedKioskAsync("config-pin", "HP LaserJet");
        using var _client = kiosk;

        var raw = await kiosk.GetStringAsync("/api/kiosks/me/config");

        // The PIN itself never leaves the server; the kiosk compares against
        // the hash (plan sections 7.1, 2.13).
        raw.Should().NotContain("135790");

        var config = JsonSerializer.Deserialize<JsonElement>(raw);
        var hash = config.GetProperty("adminPinHash").GetString();

        hash.Should().NotBeNullOrWhiteSpace();
        hash.Should().NotContain("135790");
    }

    [DockerFact]
    public async Task The_configuration_carries_no_backend_secret()
    {
        if (Skip) { return; }

        var (kiosk, _, _, _) = await ActivatedKioskAsync("config-secrets", "HP LaserJet");
        using var _client = kiosk;

        var raw = await kiosk.GetStringAsync("/api/kiosks/me/config");

        // A kiosk is an untrusted edge device: no signing key, no storage
        // credential, no SMTP password (docs/SECURITY_THREAT_MODEL.md).
        //
        // A secret would arrive as a field of that name, so the names are
        // what is searched - at every depth. Searching the whole text for
        // three letters fails by chance: the salt of the PIN hash is random
        // base64, and one day it reads "...jwT0aYTW..." (T-250).
        using var document = JsonDocument.Parse(raw);
        var names = PropertyNames(document.RootElement).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        names.Should().NotBeEmpty();

        foreach (var forbidden in new[] { "signingKey", "jwt", "secret", "roleId", "kek", "smtp", "redis", "mongo", "amqp", "password", "privateKey" })
        {
            names.Should().NotContain(
                name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"the configuration has no field about \"{forbidden}\"");
        }

        // And no connection string as a value, whatever its field is called.
        foreach (var scheme in new[] { "mongodb://", "mongodb+srv://", "redis://", "amqp://", "amqps://", "smtp://" })
        {
            raw.Should().NotContainEquivalentOf(scheme);
        }
    }

    /// <summary>Every property name in a JSON document, at any depth.</summary>
    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;

                    foreach (var inner in PropertyNames(property.Value))
                    {
                        yield return inner;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var inner in element.EnumerateArray().SelectMany(PropertyNames))
                {
                    yield return inner;
                }

                break;
        }
    }

    [DockerFact]
    public async Task The_configuration_says_which_features_are_on_for_this_kiosk()
    {
        if (Skip) { return; }

        var (kiosk, _, _, _) = await ActivatedKioskAsync("config-features", "HP LaserJet");
        using var _client = kiosk;

        var config = await kiosk.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config");

        // Evaluated for this kiosk rather than sent as raw flag definitions,
        // so the kiosk does not have to know the rollout rules.
        config.GetProperty("features").EnumerateObject().Should().NotBeEmpty();

        foreach (var feature in config.GetProperty("features").EnumerateObject())
        {
            feature.Value.ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        }
    }

    [DockerFact]
    public async Task A_kiosk_with_no_printer_yet_is_still_configured()
    {
        if (Skip) { return; }

        var (kiosk, _, _, _) = await ActivatedKioskAsync("config-no-printer");
        using var _client = kiosk;

        var config = await kiosk.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config");

        // NotConfigured is a working state: the kiosk shows screen 14 and waits
        // for an admin to pick a printer (plan section 7.3).
        config.GetProperty("status").GetString().Should().Be("NotConfigured");

        // The API omits a null rather than writing it, so "no printer" is the
        // property being absent.
        config.TryGetProperty("selectedPrinter", out var printer)
            .Should().BeFalse("there is no printer to name yet");

        config.GetProperty("effectiveCapabilities").GetProperty("color").GetBoolean()
            .Should().BeFalse("with no printer chosen, nothing is on offer");
    }

    [DockerFact]
    public async Task A_provisioning_token_cannot_read_a_configuration()
    {
        if (Skip) { return; }

        using var anonymous = _fixture.CreateApiClient();
        using var device = new FakeDevice();

        var register = await anonymous.PostAsJsonAsync("/api/devices/register", new
        {
            publicKey = device.PublicKey,
            fingerprint = device.Fingerprint("config-pending"),
            platform = "Linux",
            appVersion = "0.1.0",
        });

        var deviceId = (await register.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deviceId").GetGuid();

        var challenge = await anonymous.PostAsJsonAsync("/api/devices/challenge", new { deviceId });
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("nonce").GetString()!;

        var token = await anonymous.PostAsJsonAsync("/api/devices/token", new
        {
            deviceId,
            nonce,
            signature = device.Sign(nonce),
        });

        var issued = await token.Content.ReadFromJsonAsync<JsonElement>();
        issued.GetProperty("tokenType").GetString().Should().Be("provisioning");

        using var pending = _fixture.CreateApiClient();
        pending.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", issued.GetProperty("accessToken").GetString());

        // A device awaiting activation has no kiosk, so there is nothing to
        // configure it with.
        (await pending.GetAsync("/api/kiosks/me/config")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task A_detached_device_can_no_longer_read_the_configuration()
    {
        if (Skip) { return; }

        var (kiosk, deviceId, _, _) = await ActivatedKioskAsync("config-detach", "HP LaserJet");
        using var _client = kiosk;

        (await kiosk.GetAsync("/api/kiosks/me/config")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var admin = await SuperAdminAsync();
        await admin.PostAsJsonAsync(
            $"/api/admin/devices/{deviceId}/revoke", new { reason = "Avadanlıq dəyişdirildi" });

        // Which is what sends the kiosk back to its activation screen.
        (await kiosk.GetAsync("/api/kiosks/me/config")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // Heartbeat
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_heartbeat_records_that_the_kiosk_is_alive()
    {
        if (Skip) { return; }

        var (kiosk, deviceId, kioskId, _) = await ActivatedKioskAsync("heartbeat-basic", "HP LaserJet");
        using var _client = kiosk;

        var response = await kiosk.PostAsJsonAsync("/api/kiosks/heartbeat", new
        {
            appVersion = "0.1.1",
            agentVersion = "0.1.1",
            printerOnline = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("status").GetString().Should().Be("Online");

        // The interval comes from the settings, so it can be changed from the
        // panel without redeploying the kiosk.
        body.GetProperty("nextInSeconds").GetInt32().Should().Be(15);

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var stored = await context.Kiosks.Find(candidate => candidate.Id == kioskId).FirstAsync();
        stored.LastHeartbeatAt.Should().NotBeNull();

        // The reported version reaches both records, which is how the panel
        // shows what a kiosk is running.
        stored.AppVersion.Should().Be("0.1.1");

        var storedDevice = await context.KioskDevices
            .Find(candidate => candidate.Id == deviceId)
            .FirstAsync();

        storedDevice.AgentVersion.Should().Be("0.1.1");
    }

    [DockerFact]
    public async Task A_heartbeat_brings_an_offline_kiosk_back_without_an_operator()
    {
        if (Skip) { return; }

        var (kiosk, deviceId, kioskId, _) = await ActivatedKioskAsync("heartbeat-recover", "HP LaserJet");
        using var _client = kiosk;

        // What the heartbeat monitor does when a kiosk stops reporting.
        using (var scope = _fixture.Services.CreateScope())
        {
            var kiosks = scope.ServiceProvider.GetRequiredService<IKioskRepository>();
            var stored = await kiosks.FindByIdAsync(kioskId);

            stored!.MoveTo(
                KioskStatus.Offline,
                ActorContext.System("Heartbeat stopped"),
                DateTime.UtcNow);

            await kiosks.UpdateAsync(stored);
        }

        var response = await kiosk.PostAsJsonAsync("/api/kiosks/heartbeat", new
        {
            appVersion = "0.1.0",
            agentVersion = "0.1.0",
            printerOnline = true,
        });

        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().Should().Be("Online");

        // And the recovery is recorded with the kiosk as the actor, not a
        // person (docs/STATE_MACHINES.md).
        using var check = _fixture.Services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<MongoContext>();

        var transitions = await context.StateTransitions
            .Find(transition => transition.EntityId == kioskId && transition.ToStatus == "Online")
            .ToListAsync();

        transitions.Should().NotBeEmpty();
        transitions[^1].ActorType.Should().Be(ActorType.Kiosk);
        transitions[^1].DeviceId.Should().Be(deviceId);
    }

    [DockerFact]
    public async Task A_heartbeat_does_not_undo_a_pause_somebody_decided_on()
    {
        if (Skip) { return; }

        var (kiosk, _, kioskId, _) = await ActivatedKioskAsync("heartbeat-paused", "HP LaserJet");
        using var _client = kiosk;

        using (var scope = _fixture.Services.CreateScope())
        {
            var kiosks = scope.ServiceProvider.GetRequiredService<IKioskRepository>();
            var stored = await kiosks.FindByIdAsync(kioskId);

            stored!.MoveTo(
                KioskStatus.Paused,
                ActorContext.Admin(Guid.NewGuid(), "Stocktake"),
                DateTime.UtcNow);

            await kiosks.UpdateAsync(stored);
        }

        var response = await kiosk.PostAsJsonAsync("/api/kiosks/heartbeat", new
        {
            appVersion = "0.1.0",
            agentVersion = "0.1.0",
            printerOnline = true,
        });

        // Paused is a decision somebody made; only the kiosk going quiet is
        // undone by it coming back.
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().Should().Be("Paused");
    }

    [DockerFact]
    public async Task A_heartbeat_needs_a_kiosk_token()
    {
        if (Skip) { return; }

        using var anonymous = _fixture.CreateApiClient();

        var response = await anonymous.PostAsJsonAsync("/api/kiosks/heartbeat", new
        {
            appVersion = "0.1.0",
            agentVersion = "0.1.0",
            printerOnline = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
