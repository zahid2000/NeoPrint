using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Privacy;
using NeoPrint.Domain.Privacy;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// "Məlumatlarımı yüklə" (plan section 2.13, <c>DataExportJob</c>;
/// <c>docs/DECISIONS.md</c> T-147): one archive at a time, made in the
/// background, downloaded through a signed link for 24 hours, then deleted.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DataExportTests
{
    private readonly NeoPrintApiFixture _fixture;

    public DataExportTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [PdfiumFact]
    public async Task The_archive_is_made_once_downloaded_through_its_link_and_deleted_after_its_time()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 2);
        (await user.Client.GetAsync("/api/users/me/export")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var first = await RequestAsync(user.Client);
        first.GetProperty("status").GetString().Should().Be("Preparing");
        var again = await RequestAsync(user.Client);
        again.GetProperty("id").GetGuid().Should().Be(first.GetProperty("id").GetGuid(), "one archive at a time");

        var ready = await EventuallyAsync(
            async () => await user.Client.GetFromJsonAsync<JsonElement>("/api/users/me/export"),
            e => e.GetProperty("status").GetString() == "Ready",
            "the archive made");
        ready.GetProperty("includesFiles").GetBoolean().Should().BeTrue();
        ready.GetProperty("expiresAt").GetDateTime().ToUniversalTime().Should().BeCloseTo(DateTime.UtcNow.AddHours(24), TimeSpan.FromMinutes(2));
        var url = ready.GetProperty("downloadUrl").GetString()!;
        url.Should().StartWith("api/exports/download?token=");

        // Told, once.
        var notices = await EventuallyAsync(
            () => NotificationTests.ListAsync(user.Client, "az"),
            p => p.GetProperty("items").EnumerateArray().Any(n => n.GetProperty("type").GetString() == "ExportReady"),
            "the notification");
        notices.GetProperty("items").EnumerateArray().Single(n => n.GetProperty("type").GetString() == "ExportReady")
            .GetProperty("link").GetString().Should().Be("neoprint://privacy/export");

        // Fetched without the app's session, as the system browser does.
        using var browser = _fixture.CreateApiClient();
        var download = await browser.GetAsync("/" + url);
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");

        using (var zip = new ZipArchive(new MemoryStream(await download.Content.ReadAsByteArrayAsync())))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            names.Should().Contain(["README.txt", "profile.json", "payments.json", "refunds.json", "balance.json", "orders.json", "files.json"]);
            names.Should().ContainSingle(n => n.StartsWith("files/001 hesabat", StringComparison.Ordinal) && n.EndsWith(".pdf", StringComparison.Ordinal));

            var profile = Read(zip, "profile.json");
            profile.Should().Contain(user.Email);
            profile.Should().NotContainAny("PasswordHash", "passwordHash", "fcmTokens", "deviceIds");

            using var pdf = zip.Entries.Single(e => e.FullName.StartsWith("files/", StringComparison.Ordinal)).Open();
            var head = new byte[4];
            await pdf.ReadExactlyAsync(head);
            Encoding.ASCII.GetString(head).Should().Be("%PDF");
        }

        // A link not signed by the server, or another user's session: nothing.
        (await browser.GetAsync("/api/exports/download?token=" + Uri.EscapeDataString("AAAA" + url[^20..]))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);
        (await stranger.Client.GetAsync("/api/users/me/export")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Past its 24 hours: no link, and the clean-up deletes the archive.
        var id = ready.GetProperty("id").GetGuid();
        var stored = (await ExportAsync(id)).Archive!;
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().DataExports.UpdateOneAsync(
                Builders<DataExport>.Filter.Eq(e => e.Id, id),
                Builders<DataExport>.Update.Set(e => e.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        (await user.Client.GetFromJsonAsync<JsonElement>("/api/users/me/export")).TryGetProperty("downloadUrl", out _).Should().BeFalse();
        (await browser.GetAsync("/" + url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(new ExpireDataExportsCommand())).Should().BeGreaterThanOrEqualTo(1);
        (await ExportAsync(id)).Status.Should().Be(DataExportStatus.Expired);

        using (var scope = _fixture.Services.CreateScope())
        {
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            (await storage.ExistsAsync(DataExportHandlers.Stored(stored), StorageBucket.Temp)).Should().BeFalse();
        }

        // A new request makes a new archive.
        (await RequestAsync(user.Client)).GetProperty("id").GetGuid().Should().NotBe(id);
        _ = fileId;
    }

    [PdfiumFact]
    public async Task Files_over_the_limit_are_listed_not_included()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 1);

        using var scope = _fixture.Services.CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<IDataExportBuilder>();
        using var archive = new MemoryStream();
        var contents = await builder.BuildAsync(user.Id, "en", maxFileBytes: 10, archive, CancellationToken.None);

        contents.IncludesFiles.Should().BeFalse();
        archive.Position = 0;
        using var zip = new ZipArchive(archive);
        zip.Entries.Should().NotContain(e => e.FullName.StartsWith("files/", StringComparison.Ordinal));
        Read(zip, "files.json").Should().Contain("hesabat.pdf");
        Read(zip, "README.txt").Should().Contain("not included");
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<JsonElement> RequestAsync(HttpClient user)
    {
        var response = await user.PostAsync("/api/users/me/export", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<DataExport> ExportAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().DataExports.Find(e => e.Id == id).SingleAsync();
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }
}
