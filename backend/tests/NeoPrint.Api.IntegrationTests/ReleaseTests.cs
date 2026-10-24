using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using MongoDB.Driver;

using NeoPrint.Application.Releases;
using NeoPrint.Crypto.Releases;
using NeoPrint.Domain.Releases;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

using ReleaseComponent = NeoPrint.Crypto.Releases.ReleaseComponent;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Releases and rollouts (T-317, M16): a package is taken only when it is
/// signed for this server's key set and is the file its manifest names; a
/// rollout offers it to the kiosks it reaches and to no other; a kiosk gets
/// the very bytes the owner signed; a withdrawn package is offered to nobody.
/// </summary>
/// <remarks>
/// The server here accepts a key set made for the test - the real staging and
/// production private keys are never on a test machine.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class ReleaseTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly NeoPrintApiFixture _fixture;
    private readonly ECDsa _current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ReleaseTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    public void Dispose()
    {
        _current.Dispose();
        _next.Dispose();
        _stranger.Dispose();
    }

    /// <summary>A version no other test run has used: releases stay in the shared database.</summary>
    private static string NewVersion(int major = 1)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{major}.{RandomNumberGenerator.GetInt32(1, 1_000_000)}.{RandomNumberGenerator.GetInt32(1, 1_000_000)}");

    [DockerFact]
    public async Task A_signed_package_is_taken_once_and_every_other_kind_is_refused_with_its_reason()
    {
        if (Skip) { return; }

        await using var host = Host();
        using var admin = await AdminAsync(host);
        var version = NewVersion();

        var good = Package(version);
        var uploaded = await UploadAsync(admin, good, Sign(_current, good.Manifest));
        uploaded.StatusCode.Should().Be(HttpStatusCode.OK, await uploaded.Content.ReadAsStringAsync());
        var release = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        release.GetProperty("version").GetString().Should().Be(version);
        release.GetProperty("keySet").GetString().Should().Be("staging");
        release.GetProperty("channel").GetString().Should().Be("Pilot");
        release.GetProperty("keyId").GetString().Should().Be(ReleasePublicKey.FromKey(_current).KeyId);

        // The same package again - a retried upload: the release there is.
        var again = await UploadAsync(admin, good, Sign(_current, good.Manifest));
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid().Should().Be(release.GetProperty("id").GetGuid());

        // Another package under the same version.
        var other = Package(version, seed: 2);
        (await UploadAsync(admin, other, Sign(_current, other.Manifest))).StatusCode.Should().Be(HttpStatusCode.Conflict);

        async Task Refused(string why, TestPackage package, byte[] signature, string field, string? fileName = null)
        {
            var response = await UploadAsync(admin, package, signature, fileName);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, why + ": " + body);
            JsonDocument.Parse(body).RootElement.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue(why + ": " + body);
        }

        var fresh = Package(NewVersion());
        await Refused("a key the server does not take", fresh, Sign(_stranger, fresh.Manifest), "signature");

        var changed = fresh with { Manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fresh.Manifest).Replace("\"App\"", "\"Agent\"", StringComparison.Ordinal)) };
        await Refused("a manifest changed after signing", changed, Sign(_current, fresh.Manifest), "signature");

        var production = Package(NewVersion(), keySet: "production");
        await Refused("a package of the other key set", production, Sign(_current, production.Manifest), "signature");

        var swapped = fresh with { Bytes = [.. fresh.Bytes.Reverse()] };
        await Refused("a package that is not the manifest's", swapped, Sign(_current, fresh.Manifest), "package");

        await Refused("a package under another name", fresh, Sign(_current, fresh.Manifest), "package", fileName: "renamed.tar.gz");

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
        (await context.Releases.CountDocumentsAsync(r => r.Version == ReleaseVersionOf(fresh))).Should().Be(0, "nothing refused is kept");
    }

    [DockerFact]
    public async Task A_rollout_offers_its_package_to_the_kiosks_it_reaches_and_a_kiosk_gets_the_signed_bytes()
    {
        if (Skip) { return; }

        await using var host = Host();
        using var admin = await AdminAsync(host);
        using var reached = await ReadyKioskAsync(_fixture, "release-a");
        using var left = await ReadyKioskAsync(_fixture, "release-b");
        using var reachedApi = KioskClient(host, reached);
        using var leftApi = KioskClient(host, left);

        var first = Package(NewVersion());
        var firstSignature = Sign(_current, first.Manifest);
        var release = await (await UploadAsync(admin, first, firstSignature)).Content.ReadFromJsonAsync<JsonElement>();
        var releaseId = release.GetProperty("id").GetGuid();

        // A pilot release does not go to everyone.
        (await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId, target = "All" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var rollout = await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId, target = "Kiosks", kioskIds = new[] { reached.KioskId } });
        rollout.StatusCode.Should().Be(HttpStatusCode.OK, await rollout.Content.ReadAsStringAsync());
        var rolloutId = (await rollout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The kiosk it reaches is offered the very manifest and signature that were signed, and they check out.
        var offer = (await OffersAsync(reachedApi)).Single(o => o.GetProperty("releaseId").GetGuid() == releaseId);
        Encoding.UTF8.GetBytes(offer.GetProperty("manifest").GetString()!).Should().Equal(first.Manifest);
        Encoding.UTF8.GetBytes(offer.GetProperty("signature").GetString()!).Should().Equal(firstSignature);

        var download = await reachedApi.GetAsync("/" + offer.GetProperty("download").GetString());
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        bytes.Should().Equal(first.Bytes);

        var check = await new ReleaseVerifier([ReleasePublicKey.FromKey(_current), ReleasePublicKey.FromKey(_next)]).CheckAsync(
            first.Manifest, firstSignature, new MemoryStream(bytes),
            new ReleaseExpectation(ReleaseComponent.App, "linux-x64", "staging", ReleaseVersion.Parse("0.3.0"), ReleaseVersion.Parse("9.0.0"), []));
        check.Accepted.Should().BeTrue(check.Detail);

        // The kiosk it does not reach: nothing offered, nothing to download.
        (await OffersAsync(leftApi)).Should().NotContain(o => o.GetProperty("releaseId").GetGuid() == releaseId);
        (await leftApi.GetAsync($"/api/releases/{releaseId}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A newer release to the same kiosk: the newest is offered.
        var second = Package(NewVersion(major: 2));
        var secondId = (await (await UploadAsync(admin, second, Sign(_next, second.Manifest))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId = secondId, target = "Kiosks", kioskIds = new[] { reached.KioskId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await OffersAsync(reachedApi)).Where(o => o.GetProperty("component").GetString() == "App")
            .Select(o => o.GetProperty("releaseId").GetGuid()).Should().Contain(secondId).And.NotContain(releaseId);

        // The pilot channel: the other kiosk joins it and is reached by a channel rollout.
        (await admin.PutAsJsonAsync($"/api/admin/kiosks/{left.KioskId}/update-channel", new { channel = "Pilot" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId, target = "Channel", channel = "Pilot" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await OffersAsync(leftApi)).Should().Contain(o => o.GetProperty("releaseId").GetGuid() == releaseId);

        // Paused: the kiosks rollout no longer offers it (the channel's still does, to the pilot kiosk).
        (await admin.PostAsync($"/api/admin/rollouts/{rolloutId}/pause", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PostAsync($"/api/admin/rollouts/{rolloutId}/pause", null)).StatusCode.Should().Be(HttpStatusCode.OK, "pressed twice");

        // Withdrawn: offered to nobody, named to every kiosk as not to be installed, and its rollouts end.
        (await admin.PostAsJsonAsync($"/api/admin/releases/{releaseId}/withdraw", new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a reason is required");
        (await admin.PostAsJsonAsync($"/api/admin/releases/{releaseId}/withdraw", new { reason = "Broken printing" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var leftUpdate = await leftApi.GetFromJsonAsync<JsonElement>("/api/kiosks/me/update");
        leftUpdate.GetProperty("offers").EnumerateArray().Should().NotContain(o => o.GetProperty("releaseId").GetGuid() == releaseId);
        leftUpdate.GetProperty("withdrawn").EnumerateArray().Select(sha => sha.GetString()).Should().Contain(first.Sha256);
        (await reachedApi.GetAsync($"/api/releases/{releaseId}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var page = await admin.GetFromJsonAsync<JsonElement>("/api/admin/releases");
        page.GetProperty("rollouts").EnumerateArray().Where(r => r.GetProperty("releaseId").GetGuid() == releaseId)
            .Select(r => r.GetProperty("status").GetString()).Should().OnlyContain(status => status == "RolledBack");
        (await admin.PostAsync($"/api/admin/rollouts/{rolloutId}/resume", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "a withdrawn release is not offered again");
    }

    [DockerFact]
    public async Task A_kiosk_says_how_its_update_went_and_the_panel_sees_its_versions()
    {
        if (Skip) { return; }

        await using var host = Host();
        using var admin = await AdminAsync(host);
        using var kiosk = await ReadyKioskAsync(_fixture, "release-status");
        using var api = KioskClient(host, kiosk);

        (await api.PostAsJsonAsync("/api/kiosks/heartbeat", new { appVersion = "0.8.0", agentVersion = "0.8.1", printerOnline = true, provisioningVersion = "3" }))
            .EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/kiosks/me/update-status", new { component = "App", version = "0.9.0", status = "Failed", messageKey = "updates.signatureRefused" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.PostAsJsonAsync("/api/kiosks/me/update-status", new { component = "App", version = "not-a-version", status = "Failed" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var versions = await admin.GetFromJsonAsync<JsonElement>("/api/admin/kiosks/versions");
        var row = versions.EnumerateArray().Single(v => v.GetProperty("kioskId").GetGuid() == kiosk.KioskId);
        row.GetProperty("platform").GetString().Should().Be("linux-x64");
        row.GetProperty("appVersion").GetString().Should().Be("0.8.0");
        row.GetProperty("agentVersion").GetString().Should().Be("0.8.1");
        row.GetProperty("provisioningVersion").GetString().Should().Be("3");
        var update = row.GetProperty("updates").EnumerateArray().Single();
        update.GetProperty("status").GetString().Should().Be("Failed");
        update.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task The_agents_commands_are_sent_once_and_recorded()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "release-cmd");

        foreach (var (path, type) in new[]
                 {
                     ("update-now", "UpdateNow"), ("rollback-version", "RollbackVersion"), ("upload-logs", "UploadLogs"),
                     ("reapply-provisioning", "ReapplyProvisioning"), ("restart-agent", "RestartAgent"),
                 })
        {
            var first = await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/{path}", null);
            first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
            var command = await first.Content.ReadFromJsonAsync<JsonElement>();
            command.GetProperty("type").GetString().Should().Be(type);

            // Pressed twice: the open command is the answer, not a second one.
            var second = await (await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/{path}", null)).Content.ReadFromJsonAsync<JsonElement>();
            second.GetProperty("id").GetGuid().Should().Be(command.GetProperty("id").GetGuid());
        }

        var open = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands");
        open.EnumerateArray().Select(c => c.GetProperty("type").GetString())
            .Should().Contain(["UpdateNow", "RollbackVersion", "UploadLogs", "ReapplyProvisioning", "RestartAgent"]);
    }

    [DockerFact]
    public async Task The_server_takes_the_key_set_it_is_set_to_and_does_not_start_with_another()
    {
        if (Skip) { return; }

        await using (var staging = _fixture.WithHttpOnlyHost(builder => builder.UseSetting("Releases:KeySet", "staging")))
        {
            using var admin = await AdminAsync(staging);
            var page = await admin.GetFromJsonAsync<JsonElement>("/api/admin/releases");
            page.GetProperty("keySet").GetString().Should().Be("staging");
            page.GetProperty("trustedKeys").GetInt32().Should().Be(2);
        }

        await using (var production = _fixture.WithHttpOnlyHost(builder => builder.UseSetting("Releases:KeySet", "production")))
        {
            using var admin = await AdminAsync(production);
            (await admin.GetFromJsonAsync<JsonElement>("/api/admin/releases")).GetProperty("keySet").GetString().Should().Be("production");
        }

        await using var wrong = _fixture.WithHttpOnlyHost(builder => builder.UseSetting("Releases:KeySet", "test"));
        var start = () => wrong.CreateClient();
        start.Should().Throw<Exception>("a key set that does not exist stops the start");
    }

    [Fact]
    public void A_kiosk_keeps_its_place_in_a_percentage_rollout()
    {
        var rollout = Guid.NewGuid();
        var kiosks = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToList();

        kiosks.Select(kiosk => ReleaseRollout.Bucket(rollout, kiosk)).Should().Equal(kiosks.Select(kiosk => ReleaseRollout.Bucket(rollout, kiosk)));

        var ten = kiosks.Where(kiosk => ReleaseRollout.Bucket(rollout, kiosk) < 10).ToList();
        var thirty = kiosks.Where(kiosk => ReleaseRollout.Bucket(rollout, kiosk) < 30).ToList();
        thirty.Should().Contain(ten, "raising 10 % to 30 % keeps the first 10 %");
        ((double)ten.Count / kiosks.Count).Should().BeInRange(0.06, 0.14);
    }

    // -----------------------------------------------------------------------

    private sealed record TestPackage(byte[] Bytes, byte[] Manifest, string FileName, string Sha256);

    private static string ReleaseVersionOf(TestPackage package) => ReleaseManifest.Parse(package.Manifest).Version.ToString();

    private static TestPackage Package(string version, int seed = 1, string keySet = "staging")
    {
        var bytes = new byte[4096 + seed];
        new Random(seed).NextBytes(bytes);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var fileName = $"neoprint-kiosk-app-{version}-linux-x64-{keySet}.tar.gz";
        var manifest = new ReleaseManifest(
            ReleaseComponent.App, "linux-x64", keySet, ReleaseVersion.Parse(version), ReleaseVersion.Parse("0.1.0"),
            fileName, bytes.Length, sha256, "b6d4703", DateTimeOffset.UtcNow);
        return new TestPackage(bytes, manifest.ToBytes(), fileName, sha256);
    }

    private static byte[] Sign(ECDsa key, byte[] manifest) => ReleaseSignature.Sign(key, manifest).ToBytes();

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient admin, TestPackage package, byte[] signature, string? fileName = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(package.Bytes), "package", fileName ?? package.FileName },
            { new ByteArrayContent(package.Manifest), "manifest", package.FileName + ".manifest.json" },
            { new ByteArrayContent(signature), "signature", package.FileName + ".manifest.json.sig" },
            { new StringContent("Pilot"), "channel" },
        };
        return await admin.PostAsync("/api/admin/releases", form);
    }

    private static async Task<List<JsonElement>> OffersAsync(HttpClient kiosk)
        => [.. (await kiosk.GetFromJsonAsync<JsonElement>("/api/kiosks/me/update")).GetProperty("offers").EnumerateArray()];

    /// <summary>A second API that takes the test's key set as "staging".</summary>
    private WebApplicationFactory<Program> Host()
    {
        var keys = new TestReleaseKeys(new ReleaseKeySet("staging", [ReleasePublicKey.FromKey(_current), ReleasePublicKey.FromKey(_next)]));
        return _fixture.WithHttpOnlyHost(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IReleaseKeys>();
            services.AddSingleton<IReleaseKeys>(keys);
        }));
    }

    private static async Task<HttpClient> AdminAsync(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("accessToken").GetString());
        return client;
    }

    private static HttpClient KioskClient(WebApplicationFactory<Program> host, TestKiosk kiosk)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kiosk.AccessToken);
        return client;
    }

    private sealed class TestReleaseKeys(ReleaseKeySet accepted) : IReleaseKeys
    {
        public ReleaseKeySet Accepted { get; } = accepted;
    }
}
