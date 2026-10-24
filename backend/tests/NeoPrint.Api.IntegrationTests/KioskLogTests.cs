using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Kiosk logs (T-321, docs/KIOSK_UPDATES.md section 6): a kiosk uploads one
/// gzip file; the backend takes out personal data once more, keeps it
/// encrypted, hands it to whoever may download the kiosk's logs, refuses
/// what is not gzip or too large, and deletes it when its time is up.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class KioskLogTests
{
    private readonly NeoPrintApiFixture _fixture;

    public KioskLogTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_kiosks_logs_are_kept_without_personal_data_and_handed_to_whoever_may_download_them()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync(_fixture, "logs-upload");

        var commandId = Guid.NewGuid();
        var upload = await kiosk.Client.PostAsync($"/api/kiosks/me/logs?commandId={commandId}", Gzip(
            "2026-10-25T02:00:00Z Applying App 0.8.0-pilot.2",
            "Authorization: Bearer eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl",
            "receipt for +994 50 123 45 67 / anar@example.az",
            "Heartbeat answered in 42 ms"));
        upload.StatusCode.Should().Be(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync());
        var log = await upload.Content.ReadFromJsonAsync<JsonElement>();
        log.GetProperty("lines").GetInt32().Should().Be(4);
        log.GetProperty("redacted").GetInt32().Should().BeGreaterThanOrEqualTo(3, "this kiosk sent them uncleaned");
        log.GetProperty("commandId").GetGuid().Should().Be(commandId);
        var logId = log.GetProperty("id").GetGuid();

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/logs");
        list.EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).Should().Contain(logId);

        var file = await admin.GetAsync($"/api/admin/kiosks/{kiosk.KioskId}/logs/{logId}");
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.Should().Be("application/gzip");
        var text = Gunzip(await file.Content.ReadAsByteArrayAsync());
        text.Should().Contain("Applying App 0.8.0-pilot.2").And.Contain("Heartbeat answered in 42 ms");
        text.Should().NotContain("eyJhbGci").And.NotContain("123 45 67").And.NotContain("anar@example.az");

        // A log of another kiosk is not found under this one.
        using var other = await ReadyKioskAsync(_fixture, "logs-other");
        (await admin.GetAsync($"/api/admin/kiosks/{other.KioskId}/logs/{logId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Its time is up: file and record go.
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().KioskLogs.UpdateOneAsync(
                l => l.Id == logId, Builders<Domain.Kiosks.KioskLog>.Update.Set(l => l.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PurgeExpiredKioskLogsCommand())).Should().BeGreaterThanOrEqualTo(1);
        }

        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/logs")).EnumerateArray()
            .Select(l => l.GetProperty("id").GetGuid()).Should().NotContain(logId);
        (await admin.GetAsync($"/api/admin/kiosks/{kiosk.KioskId}/logs/{logId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task What_is_not_gzip_or_is_too_large_is_refused()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync(_fixture, "logs-refused");

        var plain = await kiosk.Client.PostAsync("/api/kiosks/me/logs", new ByteArrayContent(Encoding.UTF8.GetBytes("not compressed")));
        plain.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await plain.Content.ReadAsStringAsync()).Should().Contain("kioskLogs.notGzip");

        // Random bytes do not compress: 11 MB stays 11 MB, over the default 10.
        using var big = new MemoryStream();
        using (var gzip = new GZipStream(big, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(RandomNumberGenerator.GetBytes(11 * 1024 * 1024));
        }

        var tooLarge = await kiosk.Client.PostAsync("/api/kiosks/me/logs", new ByteArrayContent(big.ToArray()));
        tooLarge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await tooLarge.Content.ReadAsStringAsync()).Should().Contain("kioskLogs.tooLarge");
    }

    private static ByteArrayContent Gzip(params string[] lines)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        return content;
    }

    private static string Gunzip(byte[] bytes)
    {
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
