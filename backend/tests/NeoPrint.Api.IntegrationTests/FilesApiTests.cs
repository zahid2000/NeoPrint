using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Amazon.S3.Model;

using Microsoft.Extensions.DependencyInjection;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Uploading, listing, downloading and deleting a user's files
/// (plan sections 2.2, 6).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FilesApiTests
{
    private readonly NeoPrintApiFixture _fixture;

    public FilesApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    /// <summary>A signed-in user, since every endpoint here needs one.</summary>
    private async Task<HttpClient> SignedInAsync()
    {
        var client = _fixture.CreateApiClient();
        var email = $"files-{Guid.NewGuid():N}@neoprint.test";

        var register = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "FilesPassword!2026",
                fullName = "Files Tester",
                language = "az",
                deviceId = Guid.NewGuid().ToString("N"),
                consents = new[]
                {
                    new { docType = "Privacy", version = "0.1-draft" },
                    new { docType = "Terms", version = "0.1-draft" },
                },
            });

        register.StatusCode.Should().Be(HttpStatusCode.OK, await register.Content.ReadAsStringAsync());

        var code = _fixture.Emails.CodeFor(email);

        var confirm = await client.PostAsJsonAsync(
            "/api/auth/confirm-email",
            new { email, code, deviceId = Guid.NewGuid().ToString("N") });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        var token = (await confirm.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private static MultipartFormDataContent Upload(
        byte[] content, string fileName, string mediaType = "application/octet-stream")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(part, "file", fileName);

        return form;
    }

    /// <summary>A small but valid PDF, so the format check has something real.</summary>
    private static byte[] Pdf(string marker = "NeoPrint test") => Encoding.ASCII.GetBytes(
        $"%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n% {marker}\n%%EOF\n");

    [DockerFact]
    public async Task A_pdf_is_accepted_and_appears_in_the_list()
    {
        using var client = await SignedInAsync();

        var response = await client.PostAsync("/api/files", Upload(Pdf(), "hesabat.pdf", "application/pdf"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();

        created.GetProperty("extension").GetString().Should().Be("pdf");
        created.GetProperty("fileName").GetString().Should().Be("hesabat.pdf");
        created.GetProperty("status").GetString().Should().Be("Pending");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/files");

        list.GetProperty("total").GetInt64().Should().Be(1);
        list.GetProperty("items")[0].GetProperty("id").GetString()
            .Should().Be(created.GetProperty("id").GetString());
    }

    [DockerFact]
    public async Task The_content_comes_back_exactly_as_it_went_in()
    {
        using var client = await SignedInAsync();

        var original = Pdf($"round-trip {Guid.NewGuid():N}");

        var created = await (await client.PostAsync(
                "/api/files", Upload(original, "sənəd.pdf", "application/pdf")))
            .Content.ReadFromJsonAsync<JsonElement>();

        var id = created.GetProperty("id").GetString();

        var content = await client.GetByteArrayAsync($"/api/files/{id}/content");

        content.Should().Equal(original);
    }

    [DockerFact]
    public async Task What_the_store_holds_is_not_the_file()
    {
        // The end-to-end version of StorageEncryptionTests: through the API,
        // so the whole upload path is covered and not just IFileStorage.
        using var client = await SignedInAsync();

        var marker = $"gizli-{Guid.NewGuid():N}";

        var created = await (await client.PostAsync(
                "/api/files", Upload(Pdf(marker), "gizli.pdf", "application/pdf")))
            .Content.ReadFromJsonAsync<JsonElement>();

        var id = Guid.Parse(created.GetProperty("id").GetString()!);

        using var direct = _fixture.CreateDirectStorageClient();
        var listing = await direct.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = NeoPrintApiFixture.FilesBucket,
            Prefix = "files/",
        });

        var stored = listing.S3Objects!.Single(o => o.Key.Contains(id.ToString("N"), StringComparison.Ordinal));

        using var response = await direct.GetObjectAsync(
            new GetObjectRequest { BucketName = NeoPrintApiFixture.FilesBucket, Key = stored.Key });

        using var raw = new MemoryStream();
        await response.ResponseStream.CopyToAsync(raw);

        Encoding.ASCII.GetString(raw.ToArray()).Should().NotContain(marker);
        Encoding.ASCII.GetString(raw.ToArray()).Should().NotContain("%PDF");
    }

    [DockerFact]
    public async Task A_file_whose_content_does_not_match_its_name_is_refused()
    {
        using var client = await SignedInAsync();

        // A Windows executable called invoice.pdf.
        var response = await client.PostAsync(
            "/api/files",
            Upload([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00], "invoice.pdf", "application/pdf"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("tanınmadı");
    }

    [DockerFact]
    public async Task An_empty_upload_is_refused()
    {
        using var client = await SignedInAsync();

        var response = await client.PostAsync("/api/files", Upload([], "empty.pdf", "application/pdf"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task Somebody_elses_file_is_not_found_rather_than_forbidden()
    {
        using var mine = await SignedInAsync();
        using var theirs = await SignedInAsync();

        var created = await (await mine.PostAsync(
                "/api/files", Upload(Pdf(), "mənim.pdf", "application/pdf")))
            .Content.ReadFromJsonAsync<JsonElement>();

        var id = created.GetProperty("id").GetString();

        // 404, not 403: a 403 would confirm the id exists, which is enough to
        // enumerate other people's files.
        (await theirs.GetAsync($"/api/files/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await theirs.GetAsync($"/api/files/{id}/content")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await theirs.DeleteAsync($"/api/files/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task An_anonymous_caller_gets_nowhere()
    {
        using var client = _fixture.CreateApiClient();

        (await client.GetAsync("/api/files")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        (await client.PostAsync("/api/files", Upload(Pdf(), "a.pdf"))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task Deleting_removes_it_from_the_list_and_from_the_store()
    {
        using var client = await SignedInAsync();

        var created = await (await client.PostAsync(
                "/api/files", Upload(Pdf(), "silinəcək.pdf", "application/pdf")))
            .Content.ReadFromJsonAsync<JsonElement>();

        var id = Guid.Parse(created.GetProperty("id").GetString()!);

        (await client.DeleteAsync($"/api/files/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync($"/api/files/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/files");
        list.GetProperty("total").GetInt64().Should().Be(0);

        // And the bytes are really gone, not just hidden from the list.
        using var direct = _fixture.CreateDirectStorageClient();
        var listing = await direct.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = NeoPrintApiFixture.FilesBucket,
            Prefix = $"files/",
        });

        (listing.S3Objects ?? [])
            .Should().NotContain(o => o.Key.Contains(id.ToString("N"), StringComparison.Ordinal));
    }

    [DockerFact]
    public async Task A_scanned_document_defaults_to_deleting_itself_after_the_print()
    {
        using var client = await SignedInAsync();

        var form = Upload(Pdf(), "vəsiqə.pdf", "application/pdf");
        form.Add(new StringContent("Scan"), "source");
        form.Add(new StringContent("true"), "isSensitive");

        var created = await (await client.PostAsync("/api/files", form))
            .Content.ReadFromJsonAsync<JsonElement>();

        // Plan section 2.13: a personal document does not sit in the account
        // by default.
        created.GetProperty("isSensitive").GetBoolean().Should().BeTrue();
        created.GetProperty("source").GetString().Should().Be("Scan");
        created.GetProperty("autoDelete").GetString().Should().Be("AfterPrint");
    }

    [DockerFact]
    public async Task An_ordinary_upload_is_kept_until_the_user_deletes_it()
    {
        using var client = await SignedInAsync();

        var created = await (await client.PostAsync(
                "/api/files", Upload(Pdf(), "adi.pdf", "application/pdf")))
            .Content.ReadFromJsonAsync<JsonElement>();

        created.GetProperty("autoDelete").GetString().Should().Be("Never");

        var id = created.GetProperty("id").GetString();

        var changed = await client.PutAsJsonAsync(
            $"/api/files/{id}/auto-delete", new { rule = "AfterSevenDays" });

        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());

        (await changed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("autoDelete").GetString().Should().Be("AfterSevenDays");
    }

    [DockerFact]
    public async Task A_file_name_that_is_a_path_is_kept_as_a_name()
    {
        using var client = await SignedInAsync();

        var created = await (await client.PostAsync(
                "/api/files",
                Upload(Pdf(), "../../etc/passwd.pdf", "application/pdf")))
            .Content.ReadFromJsonAsync<JsonElement>();

        var name = created.GetProperty("fileName").GetString();

        name.Should().NotContain("..");
        name.Should().NotContain("/");
    }

    [DockerFact]
    public async Task The_pdf_endpoint_says_so_while_the_file_is_still_converting()
    {
        using var client = await SignedInAsync();

        var created = await (await client.PostAsync(
                "/api/files", Upload(Pdf(), "gözləyir.pdf", "application/pdf")))
            .Content.ReadFromJsonAsync<JsonElement>();

        var id = created.GetProperty("id").GetString();

        var response = await client.GetAsync($"/api/files/{id}/pdf");

        // Conversion lands later in step 2; until then this must be an
        // explained refusal rather than a 500 or an empty file.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("çevrilir");
    }

    [DockerFact]
    public async Task The_accepted_formats_are_listed_for_the_file_picker()
    {
        using var client = _fixture.CreateApiClient();

        var formats = await client.GetFromJsonAsync<string[]>("/api/files/formats");

        formats.Should().Contain("pdf").And.Contain("docx").And.Contain("jpg");
    }

    // -----------------------------------------------------------------------
    // Format administration (plan sections 2.2, 8A.3 "Fayl formatları")
    // -----------------------------------------------------------------------

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }

    private static object FormatsUpdate(IEnumerable<string> enabled) => new
    {
        enabled,
        defaultMaxSizeMb = 50,
        defaultMaxPages = 200,
        userQuotaGb = 1,
    };

    [DockerFact]
    public async Task A_switched_off_format_is_refused_at_upload()
    {
        using var admin = await SuperAdminAsync();
        using var user = await SignedInAsync();

        var formats = await admin.GetFromJsonAsync<JsonElement>("/api/admin/files/formats");
        var all = formats.GetProperty("formats").EnumerateArray()
            .Select(format => format.GetProperty("extension").GetString()!)
            .ToList();

        all.Should().Contain(["pdf", "docx", "xlsx", "pptx", "jpg", "ps", "txt"]);

        var text = Encoding.UTF8.GetBytes("Plain text to print.\n");

        try
        {
            var off = await admin.PutAsJsonAsync(
                "/api/admin/files/formats", FormatsUpdate(all.Where(extension => extension != "txt")));
            off.StatusCode.Should().Be(HttpStatusCode.OK, await off.Content.ReadAsStringAsync());

            var listed = (await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("formats")
                .EnumerateArray()
                .Single(format => format.GetProperty("extension").GetString() == "txt");
            listed.GetProperty("enabled").GetBoolean().Should().BeFalse();

            (await user.PostAsync("/api/files", Upload(text, "notes.txt", "text/plain")))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, "txt is switched off");

            // Other formats are unaffected.
            (await user.PostAsync("/api/files", Upload(Pdf(), "still-fine.pdf", "application/pdf")))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }
        finally
        {
            (await admin.PutAsJsonAsync("/api/admin/files/formats", FormatsUpdate(all)))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        (await user.PostAsync("/api/files", Upload(text, "notes.txt", "text/plain")))
            .StatusCode.Should().Be(HttpStatusCode.Created, "switched back on");
    }

    [DockerFact]
    public async Task A_format_outside_the_catalogue_cannot_be_switched_on()
    {
        using var admin = await SuperAdminAsync();

        foreach (var enabled in new[] { new[] { "pdf", "exe" }, Array.Empty<string>() })
        {
            var response = await admin.PutAsJsonAsync("/api/admin/files/formats", FormatsUpdate(enabled));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().Contain("enabled");
        }
    }

    // -----------------------------------------------------------------------
    // Page images (plan section 6, files/{id}/pages/{n}.png)
    // -----------------------------------------------------------------------

    private async Task<Guid> UploadReadyPdfAsync(HttpClient user, int pages)
    {
        var upload = await user.PostAsync(
            "/api/files", Upload(ConversionTests.OnePagePdf(pages), "preview.pdf", "application/pdf"));
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());

        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The consumer converts it too; converting is idempotent, and doing it
        // here means the test does not wait on the bus.
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<NeoPrint.Infrastructure.Conversion.DocumentConversionService>()
            .ConvertAsync(id);

        return id;
    }

    [PdfiumFact]
    public async Task A_page_is_served_as_a_png_of_the_requested_width()
    {
        using var user = await SignedInAsync();
        var id = await UploadReadyPdfAsync(user, pages: 3);

        var response = await user.GetAsync($"/api/files/{id}/pages/2.png?width=600");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Headers.CacheControl!.Private.Should().BeTrue("it is the user's document");

        var png = await response.Content.ReadAsByteArrayAsync();
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);

        // 600 is snapped up to 640; A4 keeps its aspect ratio.
        bitmap.Width.Should().Be(640);
        bitmap.Height.Should().Be((int)Math.Round(640 * 842 / 595.0));

        // Unchanged page, same tag: no second render.
        var again = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{id}/pages/2.png?width=600");
        again.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        (await user.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [PdfiumFact]
    public async Task A_page_outside_the_document_or_someone_elses_file_is_refused()
    {
        using var owner = await SignedInAsync();
        using var stranger = await SignedInAsync();
        var id = await UploadReadyPdfAsync(owner, pages: 2);

        (await owner.GetAsync($"/api/files/{id}/pages/3.png")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.GetAsync($"/api/files/{id}/pages/0.png")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await stranger.GetAsync($"/api/files/{id}/pages/1.png")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Requested_widths_snap_to_a_few_sizes()
    {
        NeoPrint.Application.Files.FilePageImage.Snap(null).Should().Be(960);
        NeoPrint.Application.Files.FilePageImage.Snap(1).Should().Be(320);
        NeoPrint.Application.Files.FilePageImage.Snap(640).Should().Be(640);
        NeoPrint.Application.Files.FilePageImage.Snap(641).Should().Be(960);
        NeoPrint.Application.Files.FilePageImage.Snap(100_000).Should().Be(1600, "a caller cannot ask for a poster");
    }
}
