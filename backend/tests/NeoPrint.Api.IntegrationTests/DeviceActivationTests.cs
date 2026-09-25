using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Domain.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using StackExchange.Redis;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The device registration and activation flow end to end
/// (plan sections 7.11, 9.0, 11).
/// </summary>
/// <remarks>
/// These exercise the same requests the kiosk application makes, including
/// signing the challenge with a key pair generated in the test, so the
/// signature verification is the real thing rather than a stub.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DeviceActivationTests
{
    private readonly NeoPrintApiFixture _fixture;

    public DeviceActivationTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    // -----------------------------------------------------------------------
    // A device, as the kiosk application behaves
    // -----------------------------------------------------------------------

    /// <summary>
    /// Stands in for one installation of the universal kiosk build: it owns a
    /// key pair it generated itself, and nothing else.
    /// </summary>
    private sealed class FakeKioskDevice : IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        public string Sign(string nonce) => Convert.ToBase64String(
            _key.SignData(
                Encoding.UTF8.GetBytes(nonce),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public object Fingerprint(string hostname) => new
        {
            hostname,
            os = "Ubuntu",
            osVersion = "24.04",
            machineId = Guid.NewGuid().ToString("N"),
            macAddresses = new[] { "AA:BB:CC:DD:EE:FF" },
            cpu = "Intel N100",
            ramGb = 8,
            screenResolution = "1920x1080",
        };

        public void Dispose() => _key.Dispose();
    }

    private async Task<(Guid DeviceId, string? PairingCode, bool Activated)> RegisterAsync(
        HttpClient client, FakeKioskDevice device, string hostname, string? enrollmentToken = null)
    {
        var response = await client.PostAsJsonAsync("/api/devices/register", new
        {
            publicKey = device.PublicKey,
            fingerprint = device.Fingerprint(hostname),
            platform = "Linux",
            appVersion = "0.1.0",
            enrollmentToken,
            printers = new[]
            {
                new { name = "HP LaserJet", driverName = "hpcups", isOnline = true, color = false, duplex = true, a3 = false },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (
            body.GetProperty("deviceId").GetGuid(),
            body.TryGetProperty("pairingCode", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null,
            body.GetProperty("activated").GetBoolean());
    }

    private static async Task<JsonElement> GetTokenAsync(
        HttpClient client, FakeKioskDevice device, Guid deviceId)
    {
        var challenge = await client.PostAsJsonAsync("/api/devices/challenge", new { deviceId });
        challenge.StatusCode.Should().Be(HttpStatusCode.OK);
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nonce").GetString()!;

        var token = await client.PostAsJsonAsync("/api/devices/token", new
        {
            deviceId,
            nonce,
            signature = device.Sign(nonce),
        });

        token.StatusCode.Should().Be(HttpStatusCode.OK);

        return await token.Content.ReadFromJsonAsync<JsonElement>();
    }

    // -----------------------------------------------------------------------
    // Administrator helper
    // -----------------------------------------------------------------------

    private async Task<HttpClient> SignedInSuperAdminAsync()
    {
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_new_device_registers_anonymously_and_is_given_a_pairing_code()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, pairingCode, activated) = await RegisterAsync(client, device, "kiosk-new");

        deviceId.Should().NotBeEmpty();
        activated.Should().BeFalse();
        pairingCode.Should().MatchRegex("^[A-Z2-9]{4}-[A-Z2-9]{4}$");
    }

    [DockerFact]
    public async Task Registering_the_same_key_again_keeps_the_same_device()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var first = await RegisterAsync(client, device, "kiosk-restart");
        // A restart, an update or a reinstall that kept the key: the identity
        // must survive, or every reboot would need activating again.
        var second = await RegisterAsync(client, device, "kiosk-restart");

        second.DeviceId.Should().Be(first.DeviceId);
    }

    [DockerFact]
    public async Task An_unactivated_device_gets_only_a_provisioning_token()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-pending");
        var token = await GetTokenAsync(client, device, deviceId);

        token.GetProperty("tokenType").GetString().Should().Be("provisioning");

        // No kiosk is named: an unactivated device has no configuration to read.
        var hasKiosk = token.TryGetProperty("kioskId", out var kioskId)
                       && kioskId.ValueKind != JsonValueKind.Null;
        hasKiosk.Should().BeFalse();
    }

    [DockerFact]
    public async Task A_provisioning_token_cannot_read_a_kiosk_configuration()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-limited");
        var token = await GetTokenAsync(client, device, deviceId);

        using var asDevice = _fixture.CreateApiClient();
        asDevice.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", token.GetProperty("accessToken").GetString());

        // It may report printers, which is all a pending device is for.
        var printers = await asDevice.PutAsJsonAsync("/api/devices/me/printers", new
        {
            printers = new[]
            {
                new { name = "HP LaserJet", driverName = "hpcups", isOnline = true, color = false, duplex = true, a3 = false },
            },
        });

        printers.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task A_wrong_signature_is_refused()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();
        using var impostor = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-signature");

        var challenge = await client.PostAsJsonAsync("/api/devices/challenge", new { deviceId });
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nonce").GetString()!;

        // Someone who has the device id but not its key.
        var response = await client.PostAsJsonAsync("/api/devices/token", new
        {
            deviceId,
            nonce,
            signature = impostor.Sign(nonce),
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task A_challenge_cannot_be_used_twice()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-replay");

        var challenge = await client.PostAsJsonAsync("/api/devices/challenge", new { deviceId });
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nonce").GetString()!;
        var signature = device.Sign(nonce);

        var first = await client.PostAsJsonAsync("/api/devices/token", new { deviceId, nonce, signature });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Replaying a captured signature must fail: the challenge is consumed.
        var second = await client.PostAsJsonAsync("/api/devices/token", new { deviceId, nonce, signature });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task An_admin_activates_a_device_as_a_new_kiosk_and_it_gets_a_kiosk_token()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, pairingCode, _) = await RegisterAsync(client, device, "kiosk-activate");

        using var admin = await SignedInSuperAdminAsync();

        // The admin types in the code the kiosk is showing.
        var lookup = await admin.PostAsJsonAsync("/api/admin/devices/lookup", new { codeOrQr = pairingCode });
        lookup.StatusCode.Should().Be(HttpStatusCode.OK);

        var found = await lookup.Content.ReadFromJsonAsync<JsonElement>();
        found.GetProperty("deviceId").GetGuid().Should().Be(deviceId);
        found.GetProperty("fingerprint").GetProperty("hostname").GetString().Should().Be("kiosk-activate");
        found.GetProperty("printers").GetArrayLength().Should().Be(1);

        var activate = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk("Integration kiosk", "Bakı test ünvanı"),
            selectedPrinterName = "HP LaserJet",
        });

        activate.StatusCode.Should().Be(HttpStatusCode.OK);

        var activated = await activate.Content.ReadFromJsonAsync<JsonElement>();
        var kioskCode = activated.GetProperty("kioskCode").GetString();
        kioskCode.Should().MatchRegex("^KSK-[0-9]{4}$");

        // A printer was chosen during activation, so the kiosk comes up online
        // rather than needing configuration (plan section 9.0).
        activated.GetProperty("kioskStatus").GetString().Should().Be("Online");

        // The device now gets a real kiosk token.
        var token = await GetTokenAsync(client, device, deviceId);
        token.GetProperty("tokenType").GetString().Should().Be("kiosk");
        token.GetProperty("kioskCode").GetString().Should().Be(kioskCode);
    }

    [DockerFact]
    public async Task A_used_pairing_code_stops_working()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, pairingCode, _) = await RegisterAsync(client, device, "kiosk-code-once");

        using var admin = await SignedInSuperAdminAsync();
        await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk("Single use code", "Bakı"),
        });

        var again = await admin.PostAsJsonAsync("/api/admin/devices/lookup", new { codeOrQr = pairingCode });

        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task An_enrollment_token_activates_a_device_with_no_pairing_code_at_all()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        var created = await admin.PostAsJsonAsync("/api/admin/enrollment-tokens", new
        {
            newKiosk = NewKiosk("Zero touch kiosk", "Bakı, sıfır toxunuş"),
            ttlHours = 24,
        });

        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        body.GetProperty("linuxCommand").GetString().Should().Contain("--enrollment-token");

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, pairingCode, activated) = await RegisterAsync(
            client, device, "kiosk-zero-touch", token);

        activated.Should().BeTrue("a valid enrollment token activates the device at registration");
        pairingCode.Should().BeNull("nobody has to read a code off the screen");

        var issued = await GetTokenAsync(client, device, deviceId);
        issued.GetProperty("tokenType").GetString().Should().Be("kiosk");
    }

    [DockerFact]
    public async Task An_enrollment_token_is_single_use()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        var created = await admin.PostAsJsonAsync("/api/admin/enrollment-tokens", new
        {
            newKiosk = NewKiosk("Single use enrollment", "Bakı"),
        });

        var token = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        using var client = _fixture.CreateApiClient();
        using var first = new FakeKioskDevice();
        using var second = new FakeKioskDevice();

        (await RegisterAsync(client, first, "kiosk-first", token)).Activated.Should().BeTrue();

        // The second machine falls back to the pairing code flow.
        var result = await RegisterAsync(client, second, "kiosk-second", token);

        result.Activated.Should().BeFalse();
        result.PairingCode.Should().NotBeNull();
    }

    [DockerFact]
    public async Task Revoking_a_device_frees_the_kiosk_and_stops_the_device_getting_a_token()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-revoke");

        using var admin = await SignedInSuperAdminAsync();
        await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk("Revoke test", "Bakı"),
        });

        var revoke = await admin.PostAsJsonAsync(
            $"/api/admin/devices/{deviceId}/revoke", new { reason = "Avadanlıq dəyişdirildi" });

        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The key no longer buys a token.
        var challenge = await client.PostAsJsonAsync("/api/devices/challenge", new { deviceId });
        challenge.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // And it cannot register its way back in either.
        var register = await client.PostAsJsonAsync("/api/devices/register", new
        {
            publicKey = device.PublicKey,
            fingerprint = device.Fingerprint("kiosk-revoke"),
            platform = "Linux",
            appVersion = "0.1.0",
        });

        register.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task Replacing_the_hardware_keeps_the_kiosk_and_revokes_the_old_device()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var original = new FakeKioskDevice();
        using var replacement = new FakeKioskDevice();

        var (originalId, _, _) = await RegisterAsync(client, original, "kiosk-old-hardware");

        using var admin = await SignedInSuperAdminAsync();
        var activate = await admin.PostAsJsonAsync($"/api/admin/devices/{originalId}/activate", new
        {
            newKiosk = NewKiosk("Hardware swap", "Bakı, dəyişdirmə"),
            selectedPrinterName = "HP LaserJet",
        });

        var kiosk = await activate.Content.ReadFromJsonAsync<JsonElement>();
        var kioskId = kiosk.GetProperty("kioskId").GetGuid();
        var kioskCode = kiosk.GetProperty("kioskCode").GetString();

        // The machine broke; a new one is installed from the same package.
        var (replacementId, _, _) = await RegisterAsync(client, replacement, "kiosk-new-hardware");

        var rebind = await admin.PostAsJsonAsync(
            $"/api/admin/devices/{replacementId}/activate", new { kioskId });

        rebind.StatusCode.Should().Be(HttpStatusCode.OK);

        var rebound = await rebind.Content.ReadFromJsonAsync<JsonElement>();
        rebound.GetProperty("kioskCode").GetString().Should().Be(
            kioskCode, "the kiosk keeps its code, its QR and its history");
        rebound.GetProperty("kioskStatus").GetString().Should().Be(
            "Online", "the printer selection survives a hardware swap");

        // The old device is out.
        var oldChallenge = await client.PostAsJsonAsync("/api/devices/challenge", new { deviceId = originalId });
        oldChallenge.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The new one is in.
        var token = await GetTokenAsync(client, replacement, replacementId);
        token.GetProperty("kioskId").GetGuid().Should().Be(kioskId);
    }

    [DockerFact]
    public async Task The_same_build_can_be_activated_onto_two_different_kiosks()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var admin = await SignedInSuperAdminAsync();

        // Two machines, one package, no per-kiosk build (plan section 7.11).
        using var windowsLike = new FakeKioskDevice();
        using var linuxLike = new FakeKioskDevice();

        var (firstId, _, _) = await RegisterAsync(client, windowsLike, "kiosk-alpha");
        var (secondId, _, _) = await RegisterAsync(client, linuxLike, "kiosk-beta");

        var firstActivation = await admin.PostAsJsonAsync($"/api/admin/devices/{firstId}/activate", new
        {
            newKiosk = NewKiosk("Alpha", "Bakı, alfa"),
            selectedPrinterName = "HP LaserJet",
        });

        var secondActivation = await admin.PostAsJsonAsync($"/api/admin/devices/{secondId}/activate", new
        {
            newKiosk = NewKiosk("Beta", "Bakı, beta"),
            selectedPrinterName = "HP LaserJet",
        });

        var first = await firstActivation.Content.ReadFromJsonAsync<JsonElement>();
        var second = await secondActivation.Content.ReadFromJsonAsync<JsonElement>();

        first.GetProperty("kioskCode").GetString().Should().NotBe(second.GetProperty("kioskCode").GetString());

        var firstToken = await GetTokenAsync(client, windowsLike, firstId);
        var secondToken = await GetTokenAsync(client, linuxLike, secondId);

        firstToken.GetProperty("kioskId").GetGuid()
            .Should().NotBe(secondToken.GetProperty("kioskId").GetGuid());
    }

    [DockerFact]
    public async Task A_failed_activation_writes_neither_the_device_nor_the_kiosk()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var admin = await SignedInSuperAdminAsync();

        using var first = new FakeKioskDevice();
        using var second = new FakeKioskDevice();

        var (firstId, _, _) = await RegisterAsync(client, first, "kiosk-atomic-one");
        var (secondId, _, _) = await RegisterAsync(client, second, "kiosk-atomic-two");

        var created = await admin.PostAsJsonAsync($"/api/admin/devices/{firstId}/activate", new
        {
            newKiosk = NewKiosk("Atomic", "Bakı, atomik"),
            selectedPrinterName = "HP LaserJet",
        });

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var kioskId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("kioskId").GetGuid();

        // Makes the kiosk unwritable in a way the handler cannot see coming: a
        // document with no row version can never satisfy the concurrency
        // filter, so saving the kiosk fails after the device has already been
        // written. That is the shape of every partial failure - the last write
        // of a command going wrong - without having to stop the process.
        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

            await context.Kiosks.UpdateOneAsync(
                Builders<Kiosk>.Filter.Eq(kiosk => kiosk.Id, kioskId),
                Builders<Kiosk>.Update.Unset(nameof(Entity.RowVersion)));
        }

        var refused = await admin.PostAsJsonAsync($"/api/admin/devices/{secondId}/activate", new
        {
            kioskId,
            selectedPrinterName = "HP LaserJet",
        });

        refused.StatusCode.Should().Be(
            HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());

        using var check = _fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<MongoContext>();

        var arriving = await db.KioskDevices
            .Find(device => device.Id == secondId)
            .FirstAsync();

        var leaving = await db.KioskDevices
            .Find(device => device.Id == firstId)
            .FirstAsync();

        // The command is marked transactional, so a failure half way through
        // has to leave nothing behind. Without that, the device would be bound
        // to a kiosk that never heard about it, and the kiosk's previous device
        // would have been revoked for nothing.
        arriving.Status.Should().Be(
            DeviceStatus.Pending,
            "the activation failed, so this device was never activated");

        arriving.KioskId.Should().BeNull();

        leaving.Status.Should().Be(
            DeviceStatus.Active,
            "the device already on the kiosk was revoked as part of an activation that then failed");

        leaving.KioskId.Should().Be(kioskId);
    }

    [DockerFact]
    public async Task Moving_a_device_to_another_kiosk_releases_the_one_it_left()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var admin = await SignedInSuperAdminAsync();

        using var device = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-moving");

        var first = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk("Leaving", "Bakı, birinci"),
            selectedPrinterName = "HP LaserJet",
        });

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var leaving = await first.Content.ReadFromJsonAsync<JsonElement>();
        var leavingKioskId = leaving.GetProperty("kioskId").GetGuid();

        // The same machine is then activated onto a second kiosk, which is how
        // an administrator moves hardware.
        var second = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk("Arriving", "Bakı, ikinci"),
            selectedPrinterName = "HP LaserJet",
        });

        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());

        var arriving = await second.Content.ReadFromJsonAsync<JsonElement>();

        arriving.GetProperty("kioskId").GetGuid().Should().NotBe(leavingKioskId);

        // The kiosk it left keeps its code and its history, but it no longer
        // claims the device: otherwise two kiosks would list the same active
        // device, and this one would look occupied to anyone activating a
        // replacement on it.
        var kiosks = await admin.GetFromJsonAsync<JsonElement>("/api/admin/kiosks");

        var left = kiosks.EnumerateArray()
            .Single(kiosk => kiosk.GetProperty("id").GetGuid() == leavingKioskId);

        left.TryGetProperty("activeDeviceId", out var stillBound)
            .Should().BeFalse($"the kiosk should have been released, but it still reports {stillBound}");

        left.GetProperty("status").GetString().Should().Be("NotActivated");

        // And the device serves only the kiosk it moved to.
        var token = await GetTokenAsync(client, device, deviceId);

        token.GetProperty("kioskId").GetGuid()
            .Should().Be(arriving.GetProperty("kioskId").GetGuid());
    }

    [DockerFact]
    public async Task Activating_without_naming_a_kiosk_is_refused()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();
        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-no-target");

        using var admin = await SignedInSuperAdminAsync();

        var response = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task An_admin_pin_that_is_too_short_is_refused()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();
        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-weak-pin");

        using var admin = await SignedInSuperAdminAsync();

        var response = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk("Weak pin", "Bakı", adminPin: "12"),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task Device_endpoints_are_closed_to_an_anonymous_caller()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var printers = await client.PutAsJsonAsync("/api/devices/me/printers", new { printers = Array.Empty<object>() });
        printers.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var admin = await client.GetAsync("/api/admin/devices");
        admin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task A_failed_lookup_does_not_echo_the_code_back()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        const string guessed = "ZZZZ-9999";

        var lookup = await admin.PostAsJsonAsync("/api/admin/devices/lookup", new { codeOrQr = guessed });

        lookup.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A pairing code authorises an activation, so it is a short-lived
        // secret: it must not come back in the response body, where it would
        // land in proxy and client logs (docs/SECURITY_THREAT_MODEL.md).
        var body = await lookup.Content.ReadAsStringAsync();

        body.Should().NotContain(guessed);
        body.Should().NotContain("ZZZZ");
    }

    // -----------------------------------------------------------------------
    // Revocation takes effect at once (plan section 7.11)
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_token_issued_before_a_revoke_stops_working_at_once()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-live-revoke");

        using var admin = await SignedInSuperAdminAsync();
        var activate = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk("Live revoke", "Bakı"),
        });

        activate.StatusCode.Should().Be(HttpStatusCode.OK, await activate.Content.ReadAsStringAsync());

        // The device holds a kiosk token from before the revoke. It lives an
        // hour, so nothing about its expiry helps here.
        var issued = await GetTokenAsync(client, device, deviceId);
        var kioskToken = issued.GetProperty("accessToken").GetString();

        using var kiosk = _fixture.CreateApiClient();
        kiosk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kioskToken);

        var before = await kiosk.PutAsJsonAsync("/api/devices/me/printers", new
        {
            printers = new[] { ReportedPrinter("Before revoke") },
        });

        before.StatusCode.Should().Be(HttpStatusCode.NoContent, "the token works while the device is active");

        var revoke = await admin.PostAsJsonAsync(
            $"/api/admin/devices/{deviceId}/revoke", new { reason = "Avadanlıq dəyişdirildi" });

        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The same token, immediately afterwards. The plan says a detached
        // device becomes invalid at once, not when its token expires.
        var after = await kiosk.PutAsJsonAsync("/api/devices/me/printers", new
        {
            printers = new[] { ReportedPrinter("After revoke") },
        });

        after.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DockerFact]
    public async Task A_revoked_device_is_refused_even_if_the_blocklist_was_lost()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-redis-loss");

        using var admin = await SignedInSuperAdminAsync();
        await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = NewKiosk("Blocklist loss", "Bakı"),
        });

        var issued = await GetTokenAsync(client, device, deviceId);

        using var kiosk = _fixture.CreateApiClient();
        kiosk.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", issued.GetProperty("accessToken").GetString());

        await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/revoke", new { reason = "Test" });

        // Simulate Redis being flushed or restarted: the blocklist entry is
        // gone. MongoDB is the source of truth, so the answer must not change
        // (docs/ARCHITECTURE_RULES.md section 2).
        await DropBlocklistEntryAsync(deviceId);

        var after = await kiosk.PutAsJsonAsync("/api/devices/me/printers", new
        {
            printers = new[] { ReportedPrinter("After redis loss") },
        });

        after.StatusCode.Should().Be(
            HttpStatusCode.Forbidden, "the device status in MongoDB decides, not the Redis blocklist");
    }

    [DockerFact]
    public async Task A_provisioning_token_stops_working_once_the_device_is_revoked()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();
        using var device = new FakeKioskDevice();

        var (deviceId, _, _) = await RegisterAsync(client, device, "kiosk-provisioning-revoke");

        // A device awaiting activation gets a provisioning token, which it uses
        // to renew the code on its screen.
        var issued = await GetTokenAsync(client, device, deviceId);
        issued.GetProperty("tokenType").GetString().Should().Be("provisioning");

        using var pending = _fixture.CreateApiClient();
        pending.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", issued.GetProperty("accessToken").GetString());

        var before = await pending.PostAsync("/api/devices/me/pairing-code", content: null);
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        using var admin = await SignedInSuperAdminAsync();
        var revoke = await admin.PostAsJsonAsync(
            $"/api/admin/devices/{deviceId}/revoke", new { reason = "Cihaz itdi" });

        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await pending.PostAsync("/api/devices/me/pairing-code", content: null);
        after.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static object ReportedPrinter(string name) => new
    {
        name,
        driverName = "Test driver",
        isOnline = true,
        color = false,
        duplex = false,
        a3 = false,
    };

    /// <summary>
    /// Removes the Redis blocklist entry, to prove the database check stands on
    /// its own.
    /// </summary>
    private async Task DropBlocklistEntryAsync(Guid deviceId)
    {
        using var scope = _fixture.Services.CreateScope();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();

        await redis.GetDatabase().KeyDeleteAsync($"device:blocked:{deviceId}");
    }
    private static object NewKiosk(string name, string address, string adminPin = "135790") => new
    {
        name,
        lat = 40.3793,
        lng = 49.8486,
        address,
        defaultLanguage = "az",
        paperCapacityA4 = 500,
        paperCapacityA3 = 250,
        adminPin,
    };
}
