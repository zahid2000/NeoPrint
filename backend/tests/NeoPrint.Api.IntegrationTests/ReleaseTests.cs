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
            // They change what runs on the kiosk: not without a reason (T-326).
            var without = await admin.PostAsync($"/api/admin/kiosks/{kiosk.KioskId}/{path}", null);
            without.StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
            (await without.Content.ReadAsStringAsync()).Should().Contain("reason");
            (await admin.PostAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/{path}", new { reason = "ok" })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "too short");

            var first = await admin.PostAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/{path}", Because);
            first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
            var command = await first.Content.ReadFromJsonAsync<JsonElement>();
            command.GetProperty("type").GetString().Should().Be(type);
            command.GetProperty("reason").GetString().Should().Be(BecauseText);

            // Pressed twice: the open command is the answer, not a second one.
            var second = await (await admin.PostAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/{path}", Because)).Content.ReadFromJsonAsync<JsonElement>();
            second.GetProperty("id").GetGuid().Should().Be(command.GetProperty("id").GetGuid());
        }

        var open = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/commands");
        open.EnumerateArray().Select(c => c.GetProperty("type").GetString())
            .Should().Contain(["UpdateNow", "RollbackVersion", "UploadLogs", "ReapplyProvisioning", "RestartAgent"]);

        // The reason is the administrators' note: the kiosk is never told it.
        open.EnumerateArray().Select(c => c.TryGetProperty("reason", out var reason) ? reason.ValueKind : JsonValueKind.Null)
            .Should().OnlyContain(kind => kind == JsonValueKind.Null);

        // And it is on the record.
        using var scope = _fixture.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<MongoContext>().AuditLogs
            .Find(entry => entry.EntityId == kiosk.KioskId.ToString() && entry.Action == "kiosks.command.RollbackVersion").ToListAsync();
        audit.Should().Contain(entry => entry.Reason == BecauseText);
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

    [DockerFact]
    public async Task A_version_a_kiosk_took_back_raises_an_incident_and_is_not_offered_to_it_again_until_update_now()
    {
        if (Skip) { return; }

        await using var host = Host();
        using var admin = await AdminAsync(host);
        using var kiosk = await ReadyKioskAsync(_fixture, "release-rollback");
        using var api = KioskClient(host, kiosk);

        var package = Package(NewVersion(major: 3), seed: 7);
        var version = ReleaseVersionOf(package);
        var releaseId = (await (await UploadAsync(admin, package, Sign(_current, package.Manifest))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        // A security fix: the kiosk is told not to wait for the update window.
        var rollout = await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId, target = "Kiosks", kioskIds = new[] { kiosk.KioskId }, urgent = true });
        rollout.StatusCode.Should().Be(HttpStatusCode.OK, await rollout.Content.ReadAsStringAsync());
        (await rollout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("urgent").GetBoolean().Should().BeTrue();
        (await OffersAsync(api)).Single(o => o.GetProperty("releaseId").GetGuid() == releaseId).GetProperty("urgent").GetBoolean().Should().BeTrue();

        // Waiting for a moment with no customer, then turning new ones away: both are shown to the panel.
        foreach (var status in new[] { "WaitingForIdle", "Draining", "Applying", "HealthCheck" })
        {
            (await api.PostAsJsonAsync("/api/kiosks/me/update-status", new { component = "App", version, status }))
                .StatusCode.Should().Be(HttpStatusCode.NoContent, status);
        }

        // It failed its health check and went back: High incident, panel and e-mail; not offered again.
        (await api.PostAsJsonAsync("/api/kiosks/me/update-status", new { component = "App", version, status = "RolledBack", messageKey = "updates.healthTimeout" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.PostAsJsonAsync("/api/kiosks/me/update-status", new { component = "App", version, status = "RolledBack", messageKey = "updates.healthTimeout" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "said twice");

        var incidents = await IncidentsAsync(kiosk.KioskId);
        incidents.Should().ContainSingle(i => i.Type == Domain.Incidents.IncidentType.UpdateFailed);
        var incident = incidents.Single(i => i.Type == Domain.Incidents.IncidentType.UpdateFailed);
        incident.Severity.Should().Be(Domain.Incidents.IncidentSeverity.High);
        incident.Details.Should().Contain("incidents.kiosk.updateRolledBack").And.Contain(version).And.Contain("reasonMessage=updates.healthTimeout");
        Application.Localization.Messages.Write(Domain.Common.UserMessage.Decode(incident.Details)).Should().Contain(version).And.NotContain("{");

        (await OffersAsync(api)).Should().NotContain(o => o.GetProperty("releaseId").GetGuid() == releaseId);
        (await api.GetAsync($"/api/releases/{releaseId}/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // "İndi yenilə": an administrator asks for it again on purpose.
        (await admin.PostAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/update-now", Because)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await OffersAsync(api)).Should().Contain(o => o.GetProperty("releaseId").GetGuid() == releaseId);

        // Taken back by an administrator ("Əvvəlki versiyaya qaytar"): not an incident.
        var other = NewVersion(major: 4);
        (await api.PostAsJsonAsync("/api/kiosks/me/update-status", new { component = "Agent", version = other, status = "RolledBack", messageKey = "updates.rolledBackByAdmin" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await IncidentsAsync(kiosk.KioskId)).Should().ContainSingle(i => i.Type == Domain.Incidents.IncidentType.UpdateFailed);
    }

    [DockerFact]
    public async Task A_download_cut_off_goes_on_from_where_it_stopped_and_the_kiosk_learns_when_to_update()
    {
        if (Skip) { return; }

        await using var host = Host();
        using var admin = await AdminAsync(host);
        using var kiosk = await ReadyKioskAsync(_fixture, "release-range");
        using var api = KioskClient(host, kiosk);

        var package = Package(NewVersion(major: 5), seed: 900);
        var releaseId = (await (await UploadAsync(admin, package, Sign(_current, package.Manifest))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId, target = "Kiosks", kioskIds = new[] { kiosk.KioskId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await OffersAsync(api)).Single(o => o.GetProperty("releaseId").GetGuid() == releaseId).GetProperty("urgent").GetBoolean().Should().BeFalse();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/releases/{releaseId}/download");
        request.Headers.Range = new RangeHeaderValue(1000, null);
        var tail = await api.SendAsync(request);
        tail.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        tail.Content.Headers.ContentRange!.From.Should().Be(1000);
        tail.Content.Headers.ContentRange.Length.Should().Be(package.Bytes.Length);
        (await tail.Content.ReadAsByteArrayAsync()).Should().Equal(package.Bytes[1000..]);

        foreach (var range in new[] { new RangeHeaderValue(package.Bytes.Length, null), new RangeHeaderValue(0, 10) })
        {
            using var wrong = new HttpRequestMessage(HttpMethod.Get, $"/api/releases/{releaseId}/download");
            wrong.Headers.Range = range;
            (await api.SendAsync(wrong)).StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable, range.ToString());
        }

        // When and how: the update window and the timings come with the configuration.
        var update = (await api.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config")).GetProperty("update");
        update.GetProperty("window").GetString().Should().MatchRegex(@"^\d{2}:\d{2}-\d{2}:\d{2}$");
        update.GetProperty("maxIdleWaitMinutes").GetInt32().Should().BePositive();
        update.GetProperty("healthTimeoutMinutes").GetInt32().Should().BePositive();
        update.GetProperty("keepVersions").GetInt32().Should().BePositive();
    }

    [DockerFact]
    public async Task An_update_window_moved_in_the_panel_reaches_the_connected_kiosks_at_once()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "release-window");
        await using var hub = await RawHubConnection.ConnectAsync(_fixture, "kiosk", kiosk.AccessToken);
        var before = (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config")).GetProperty("update").GetProperty("window").GetString();
        var moved = before == "01:00-04:00" ? "02:00-05:00" : "01:00-04:00";

        try
        {
            // T-326: found in the end-to-end check - the kiosk went on with the old window until its next poll.
            (await admin.PutAsJsonAsync("/api/admin/settings", new { values = new Dictionary<string, string> { ["Kiosk.UpdateWindow"] = moved } }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            await hub.WaitForAsync("KioskConfigChanged", TimeSpan.FromSeconds(10));
            (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config")).GetProperty("update").GetProperty("window").GetString().Should().Be(moved);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/settings", new { values = new Dictionary<string, string> { ["Kiosk.UpdateWindow"] = before! } });
        }
    }

    [DockerFact]
    public async Task A_package_a_kiosk_rejects_is_a_security_incident_counted_not_reopened_and_not_offered_again()
    {
        if (Skip) { return; }

        await using var host = Host();
        using var admin = await AdminAsync(host);
        using var kiosk = await ReadyKioskAsync(_fixture, "release-rejected");
        using var api = KioskClient(host, kiosk);

        var package = Package(NewVersion(major: 5), seed: 11);
        var version = ReleaseVersionOf(package);
        var releaseId = (await (await UploadAsync(admin, package, Sign(_current, package.Manifest))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        (await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId, target = "Kiosks", kioskIds = new[] { kiosk.KioskId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Task<HttpResponseMessage> Report(string reported, string messageKey)
            => api.PostAsJsonAsync("/api/kiosks/me/update-status", new { component = "App", version = reported, status = "Failed", messageKey });

        // The signature did not check out - twice: one incident, seen twice.
        (await Report(version, "updates.signatureRefused")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await Task.Delay(10);
        (await Report(version, "updates.signatureRefused")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rejected = (await IncidentsAsync(kiosk.KioskId)).Where(i => i.Type == Domain.Incidents.IncidentType.UpdatePackageRejected).ToList();
        rejected.Should().ContainSingle();
        rejected[0].Severity.Should().Be(Domain.Incidents.IncidentSeverity.High);
        rejected[0].Occurrences.Should().Be(2);
        rejected[0].Quiet.Should().BeFalse("the first of the hour is e-mailed");
        rejected[0].EntityId.Should().Be(releaseId);
        var text = Application.Localization.Messages.Write(Domain.Common.UserMessage.Decode(rejected[0].Details));
        text.Should().Contain(version).And.Contain("bir daha təklif olunmur").And.NotContain("növbəti yoxlamada").And.NotContain("{");
        (await IncidentsAsync(kiosk.KioskId)).Should().NotContain(i => i.Type == Domain.Incidents.IncidentType.UpdateFailed, "a security matter, not a failed update");

        // Not offered again - it would only be refused again - until "İndi yenilə".
        (await OffersAsync(api)).Should().NotContain(o => o.GetProperty("releaseId").GetGuid() == releaseId);

        // Another reason: its own incident, shown in the panel but not e-mailed within the hour.
        (await Report(version, "updates.packageMismatch")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var mismatch = (await IncidentsAsync(kiosk.KioskId)).Single(i => i.DedupKey.EndsWith(":updates.packageMismatch", StringComparison.Ordinal));
        mismatch.Quiet.Should().BeTrue();
        using (var notify = _fixture.Services.CreateScope())
        {
            (await notify.ServiceProvider.GetRequiredService<MediatR.ISender>().Send(new Application.Incidents.NotifyIncidentCommand(mismatch.Id)))
                .Should().Be(0, "a quiet incident is e-mailed to nobody");
        }

        // Versions made up on the kiosk are no release here: one "unknown" incident, however many names.
        foreach (var madeUp in new[] { "9.9.1", "9.9.2", "9.9.3" })
        {
            (await Report(madeUp, "updates.signatureRefused")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        var unknown = (await IncidentsAsync(kiosk.KioskId))
            .Where(i => i.Type == Domain.Incidents.IncidentType.UpdatePackageRejected && i.DedupKey.Contains(":unknown:", StringComparison.Ordinal)).ToList();
        unknown.Should().ContainSingle();
        unknown[0].Occurrences.Should().Be(3);
        unknown[0].Details.Should().Contain("incidents.kiosk.updatePackageRejectedUnknown");

        (await admin.PostAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/update-now", Because)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await OffersAsync(api)).Should().Contain(o => o.GetProperty("releaseId").GetGuid() == releaseId);
    }

    [DockerFact]
    public async Task A_kiosks_update_tab_shows_its_versions_offers_and_history_to_whoever_sees_the_kiosk_and_the_rollout_its_progress()
    {
        if (Skip) { return; }

        await using var host = Host();
        using var admin = await AdminAsync(host);
        using var kiosk = await ReadyKioskAsync(_fixture, "release-detail");
        using var other = await ReadyKioskAsync(_fixture, "release-detail-other");
        using var api = KioskClient(host, kiosk);

        var package = Package(NewVersion(major: 6), seed: 13);
        var version = ReleaseVersionOf(package);
        var releaseId = (await (await UploadAsync(admin, package, Sign(_current, package.Manifest))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        var rolloutId = (await (await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId, target = "Kiosks", kioskIds = new[] { kiosk.KioskId, other.KioskId } }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        foreach (var status in new[] { "Downloading", "Ready" })
        {
            (await api.PostAsJsonAsync("/api/kiosks/me/update-status", new { component = "App", version, status })).StatusCode.Should().Be(HttpStatusCode.NoContent);
            await Task.Delay(10);
        }

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/update");
        detail.GetProperty("hasDevice").GetBoolean().Should().BeTrue();
        detail.GetProperty("maxIdleWaitMinutes").GetInt32().Should().BePositive();
        var app = detail.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("component").GetString() == "App");
        app.GetProperty("target").GetString().Should().Be(version);
        app.GetProperty("state").GetProperty("status").GetString().Should().Be("Ready");
        detail.GetProperty("components").GetArrayLength().Should().Be(3);
        detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("status").GetString()).Should().StartWith(["Ready", "Downloading"]);

        // The rollout: one of its two kiosks is ready, none runs it yet.
        var page = await admin.GetFromJsonAsync<JsonElement>("/api/admin/releases");
        var progress = page.GetProperty("rollouts").EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == rolloutId).GetProperty("progress");
        progress.GetProperty("ready").GetInt32().Should().Be(1);
        progress.GetProperty("installed").GetInt32().Should().Be(0);

        // An account that sees only this kiosk sees its tab - not another kiosk's, nor the releases page.
        using var scoped = await ScopedAsync(host, Domain.Admin.KioskScope.Only(kiosk.KioskId), Domain.Admin.Permissions.KiosksView);
        (await scoped.GetAsync($"/api/admin/kiosks/{kiosk.KioskId}/update")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await scoped.GetAsync($"/api/admin/kiosks/{other.KioskId}/update")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await scoped.GetAsync("/api/admin/releases")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await scoped.PostAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/update-now", Because)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "seeing is not commanding");

        // With the update permission it may command its own kiosk - not another.
        using var commander = await ScopedAsync(
            host, Domain.Admin.KioskScope.Only(kiosk.KioskId), Domain.Admin.Permissions.KiosksView, Domain.Admin.Permissions.KioskCommandsUpdate);
        (await commander.PostAsJsonAsync($"/api/admin/kiosks/{kiosk.KioskId}/update-now", Because)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await commander.PostAsJsonAsync($"/api/admin/kiosks/{other.KioskId}/update-now", Because)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private const string BecauseText = "Integration test: checking the command";

    private static object Because => new { reason = BecauseText };

    private async Task<HttpClient> ScopedAsync(WebApplicationFactory<Program> host, Domain.Admin.KioskScope scope, params string[] permissions)
    {
        const string password = "ReleaseScope!2026";
        using var superAdmin = await AdminAsync(host);
        var role = await superAdmin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Release scope {Guid.NewGuid():N}",
            description = "Created by the integration tests.",
            permissions,
        });
        role.StatusCode.Should().Be(HttpStatusCode.OK, await role.Content.ReadAsStringAsync());

        using var services = _fixture.Services.CreateScope();
        var hasher = services.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<Domain.Admin.AdminUser>>();
        var account = new Domain.Admin.AdminUser
        {
            Email = $"release.{Guid.NewGuid():N}@neoprint.test",
            FullName = "Release scope",
            RoleIds = [(await role.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid()],
            KioskScope = scope,
            IsActive = true,
        };
        account.PasswordHash = hasher.HashPassword(account, password);
        await services.ServiceProvider.GetRequiredService<MongoContext>().AdminUsers.InsertOneAsync(account);

        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email = account.Email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("accessToken").GetString());
        return client;
    }

    private async Task<List<Domain.Incidents.Incident>> IncidentsAsync(Guid kioskId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Incidents.Find(i => i.KioskId == kioskId).ToListAsync();
    }

    [DockerFact]
    public async Task The_newest_release_is_given_to_anybody_only_when_it_is_published_production_and_never_taken_back()
    {
        if (Skip) { return; }

        // T-327: a provisioning script on a kiosk with no identity yet fetches from here.
        await using var production = Host("production");
        using var admin = await AdminAsync(production);
        using var anybody = production.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var older = Package(NewVersion(3), seed: 31, keySet: "production");
        var newer = Package(ReleaseManifest.Parse(older.Manifest).Version.Major + "." + (ReleaseManifest.Parse(older.Manifest).Version.Minor + 1) + ".0", seed: 32, keySet: "production");
        var olderId = (await (await UploadAsync(admin, older, Sign(_current, older.Manifest))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var newerId = (await (await UploadAsync(admin, newer, Sign(_current, newer.Manifest))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        async Task<string?> LatestAsync(string? channel = null)
        {
            var response = await anybody.GetAsync($"/api/releases/latest/app/linux-x64{(channel is null ? "" : $"?channel={channel}")}");
            return response.StatusCode == HttpStatusCode.NotFound
                ? null
                : (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString();
        }

        // Both are on the pilot channel: published to nobody who asks for stable.
        (await LatestAsync()).Should().NotBe(ReleaseVersionOf(older)).And.NotBe(ReleaseVersionOf(newer));
        (await LatestAsync("pilot")).Should().Be(ReleaseVersionOf(newer));

        (await admin.PostAsync($"/api/admin/releases/{olderId}/promote", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LatestAsync()).Should().Be(ReleaseVersionOf(older));

        var latest = await anybody.GetFromJsonAsync<JsonElement>("/api/releases/latest/app/linux-x64?channel=pilot");
        latest.GetProperty("keySet").GetString().Should().Be("production");
        latest.GetProperty("sha256").GetString().Should().Be(newer.Sha256);
        latest.TryGetProperty("uploadedBy", out _).Should().BeFalse("nothing of who uploaded it");
        latest.TryGetProperty("rollouts", out _).Should().BeFalse("nor where it went");

        // The three files, byte for byte what was signed.
        var path = latest.GetProperty("manifestPath").GetString()![..^"/manifest".Length];
        (await anybody.GetByteArrayAsync($"/{path}/manifest")).Should().Equal(newer.Manifest);
        (await anybody.GetByteArrayAsync($"/{path}/signature")).Should().NotBeEmpty();
        (await anybody.GetByteArrayAsync($"/{path}/package")).Should().Equal(newer.Bytes);
        (await anybody.GetAsync($"/{path}/secrets")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var check = await new ReleaseVerifier([ReleasePublicKey.FromKey(_current), ReleasePublicKey.FromKey(_next)]).CheckAsync(
            await anybody.GetByteArrayAsync($"/{path}/manifest"),
            await anybody.GetByteArrayAsync($"/{path}/signature"),
            new MemoryStream(await anybody.GetByteArrayAsync($"/{path}/package")),
            new ReleaseExpectation(ReleaseComponent.App, "linux-x64", "production", null, ReleaseVersion.Parse("9.0.0"), []));
        check.Accepted.Should().BeTrue("what is fetched passes the script's own check: {0}", check.Detail);

        // Withdrawn: gone, files and all.
        (await admin.PostAsJsonAsync($"/api/admin/releases/{newerId}/withdraw", new { reason = "Broken on the pilot" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LatestAsync("pilot")).Should().Be(ReleaseVersionOf(older));
        (await anybody.GetAsync($"/{path}/package")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A rollout of it taken back: not given to a new kiosk either.
        var kiosk = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/kiosks")).EnumerateArray().First().GetProperty("id").GetGuid();
        var rollout = await admin.PostAsJsonAsync("/api/admin/rollouts", new { releaseId = olderId, target = "Kiosks", kioskIds = new[] { kiosk } });
        rollout.StatusCode.Should().Be(HttpStatusCode.OK, await rollout.Content.ReadAsStringAsync());
        var rolloutId = (await rollout.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/admin/rollouts/{rolloutId}/rollback", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LatestAsync()).Should().NotBe(ReleaseVersionOf(older));

        (await anybody.GetAsync("/api/releases/latest/app/linux-x64?channel=nightly")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await anybody.GetAsync("/api/releases/latest/kernel/linux-x64")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_server_that_takes_staging_packages_gives_nobody_a_release()
    {
        if (Skip) { return; }

        await using var staging = Host();
        using var admin = await AdminAsync(staging);
        var package = Package(NewVersion(4), seed: 41);
        var id = (await (await UploadAsync(admin, package, Sign(_current, package.Manifest))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await admin.PostAsync($"/api/admin/releases/{id}/promote", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var anybody = staging.CreateClient();
        (await anybody.GetAsync("/api/releases/latest/app/linux-x64")).StatusCode.Should().Be(HttpStatusCode.NotFound, "a staging package goes to no new kiosk");
        (await anybody.GetAsync($"/api/releases/app/linux-x64/{ReleaseVersionOf(package)}/package")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Fetching_releases_without_signing_in_is_limited_per_address()
    {
        if (Skip) { return; }

        await using var limited = Host("production", builder => builder
            .UseSetting("RateLimits:Disabled", "false")
            .UseSetting("RateLimits:ReleaseDownloadPerHour", "3"));
        using var anybody = limited.CreateClient();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            codes.Add((await anybody.GetAsync("/api/releases/latest/agent/win-x64")).StatusCode);
        }

        codes.Take(3).Should().NotContain(HttpStatusCode.TooManyRequests);
        codes[3].Should().Be(HttpStatusCode.TooManyRequests);
    }

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

    /// <summary>A second API that takes the test's key set under a name: "staging" unless told otherwise.</summary>
    private WebApplicationFactory<Program> Host(string keySet = "staging", Action<IWebHostBuilder>? configure = null)
    {
        var keys = new TestReleaseKeys(new ReleaseKeySet(keySet, [ReleasePublicKey.FromKey(_current), ReleasePublicKey.FromKey(_next)]));
        return _fixture.WithHttpOnlyHost(builder =>
        {
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IReleaseKeys>();
                services.AddSingleton<IReleaseKeys>(keys);
            });
        });
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
