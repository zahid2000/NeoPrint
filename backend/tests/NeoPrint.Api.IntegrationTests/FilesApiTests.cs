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

            // The list the mobile picker filters by leaves it out too.
            using var anonymous = _fixture.CreateApiClient();
            (await anonymous.GetFromJsonAsync<string[]>("/api/files/formats"))
                .Should().Contain("pdf").And.NotContain("txt");

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
    public async Task The_size_limit_is_the_default_unless_the_kiosk_has_its_own()
    {
        // Plan section 2.2: effective limit = kiosk.MaxFileSizeMb ?? Files.DefaultMaxSizeMb.
        using var admin = await SuperAdminAsync();
        using var user = await SignedInAsync();

        var formats = await admin.GetFromJsonAsync<JsonElement>("/api/admin/files/formats");
        var all = formats.GetProperty("formats").EnumerateArray()
            .Where(format => format.GetProperty("enabled").GetBoolean())
            .Select(format => format.GetProperty("extension").GetString()!)
            .ToList();

        var kiosk = await admin.PostAsJsonAsync("/api/admin/kiosks", new
        {
            name = "Limit kiosk",
            lat = 40.4,
            lng = 49.8,
            address = "Bakı",
            defaultLanguage = "az",
            maxFileSizeMb = 3,
            paperCapacityA4 = 500,
            paperCapacityA3 = 0,
            adminPin = "482915",
        });
        kiosk.StatusCode.Should().Be(HttpStatusCode.Created, await kiosk.Content.ReadAsStringAsync());
        var kioskId = (await kiosk.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // 1.5 MB: over a 1 MB default, under the kiosk's 3 MB.
        var padding = Enumerable.Repeat((byte)'%', (int)(1.5 * 1024 * 1024)).ToArray();
        var large = Pdf().Concat(padding).ToArray();

        MultipartFormDataContent ForKiosk()
        {
            var form = Upload(large, "large.pdf", "application/pdf");
            form.Add(new StringContent(kioskId!), "kioskId");
            return form;
        }

        try
        {
            (await admin.PutAsJsonAsync("/api/admin/files/formats", new
            {
                enabled = all,
                defaultMaxSizeMb = 1,
                defaultMaxPages = 200,
                userQuotaGb = 1,
            })).StatusCode.Should().Be(HttpStatusCode.OK);

            var refused = await user.PostAsync("/api/files", Upload(large, "large.pdf", "application/pdf"));
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("maksimum 1");

            (await user.PostAsync("/api/files", ForKiosk()))
                .StatusCode.Should().Be(HttpStatusCode.Created, "the kiosk allows 3 MB");
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/files/formats", new
            {
                enabled = all,
                defaultMaxSizeMb = formats.GetProperty("defaultMaxSizeMb").GetInt32(),
                defaultMaxPages = formats.GetProperty("defaultMaxPages").GetInt32(),
                userQuotaGb = formats.GetProperty("userQuotaGb").GetInt32(),
            });
        }
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

        await ConvertAsync(id);

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

    // -----------------------------------------------------------------------
    // Editing and versions (plan section 2.2, POST files/{id}/edit)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Waits until the file is settled - Ready or Failed - converting it here
    /// when the bus has not got to it yet.
    /// </summary>
    /// <remarks>
    /// The conversion consumer receives the same file at the same time. Two
    /// conversions of one file meet on the row version or on the state
    /// machine, and one of them gives way; whichever wins, the file ends up
    /// settled, which is what the tests wait for.
    /// </remarks>
    private async Task<NeoPrint.Domain.Files.FileStatus> ConvertAsync(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            using var scope = _fixture.Services.CreateScope();
            var file = await scope.ServiceProvider
                .GetRequiredService<NeoPrint.Application.Files.IFileRepository>()
                .FindByIdAsync(id);

            if (file!.Status is NeoPrint.Domain.Files.FileStatus.Ready or NeoPrint.Domain.Files.FileStatus.Failed)
            {
                return file.Status;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"File {id} is still {file.Status}.");
            }

            try
            {
                await scope.ServiceProvider
                    .GetRequiredService<NeoPrint.Infrastructure.Conversion.DocumentConversionService>()
                    .ConvertAsync(id);
            }
            catch (NeoPrint.Domain.Common.DomainException)
            {
                // The consumer is converting it: wait for that instead.
                await Task.Delay(200);
            }
        }
    }

    private static async Task<JsonElement> FileAsync(HttpClient user, Guid id)
        => await user.GetFromJsonAsync<JsonElement>($"/api/files/{id}");

    private static byte[] Png(int width, int height, SkiaSharp.SKColor color)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [PdfiumFact]
    public async Task A_page_edit_becomes_a_new_version_and_the_original_stays()
    {
        using var user = await SignedInAsync();
        var original = ConversionTests.OnePagePdf(3);

        var upload = await user.PostAsync("/api/files", Upload(original, "hesabat.pdf", "application/pdf"));
        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await ConvertAsync(id);

        // Keep pages 3 and 1, in that order, the first turned a quarter.
        var edit = await user.PostAsJsonAsync($"/api/files/{id}/edit", new
        {
            pages = new[] { new { page = 3, rotate = 90 }, new { page = 1, rotate = 0 } },
        });

        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());
        (await edit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString()
            .Should().Be("Pending", "the edit is converted like an upload");

        await ConvertAsync(id);

        var file = await FileAsync(user, id);
        file.GetProperty("status").GetString().Should().Be("Ready");
        file.GetProperty("pageCount").GetInt32().Should().Be(2, "the price follows the edit");
        file.GetProperty("versionCount").GetInt32().Should().Be(1);

        // The original is untouched.
        (await user.GetByteArrayAsync($"/api/files/{id}/content")).Should().Equal(original);

        var versions = await user.GetFromJsonAsync<JsonElement>($"/api/files/{id}/versions");
        versions.GetArrayLength().Should().Be(1);
        versions[0].GetProperty("extension").GetString().Should().Be("pdf");
        versions[0].GetProperty("isCurrent").GetBoolean().Should().BeTrue();
        versions[0].GetProperty("note").GetString().Should().Contain("3 səhifədən 2-i").And.Contain("fırlandı");

        var saved = await user.GetByteArrayAsync($"/api/files/{id}/versions/1/content");
        using var document = PdfSharp.Pdf.IO.PdfReader.Open(
            new MemoryStream(saved), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        document.PageCount.Should().Be(2);
        document.Pages[0].Rotate.Should().Be(90);
        document.Pages[1].Rotate.Should().Be(0);
    }

    [PdfiumFact]
    public async Task An_image_edit_crops_turns_and_greys_and_the_old_pdf_is_removed()
    {
        using var user = await SignedInAsync();

        var upload = await user.PostAsync(
            "/api/files", Upload(Png(40, 20, new SkiaSharp.SKColor(200, 40, 40)), "foto.png", "image/png"));
        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await ConvertAsync(id);

        var edit = await user.PostAsJsonAsync($"/api/files/{id}/edit", new
        {
            image = new { rotate = 90, crop = new { x = 0.0, y = 0.0, width = 0.5, height = 1.0 }, grayscale = true },
        });
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());

        await ConvertAsync(id);
        (await FileAsync(user, id)).GetProperty("status").GetString().Should().Be("Ready");

        using var bitmap = SkiaSharp.SKBitmap.Decode(
            await user.GetByteArrayAsync($"/api/files/{id}/versions/1/content"));

        // Cropped to 20x20, then turned: still 20x20; grey means r = g = b.
        bitmap.Width.Should().Be(20);
        bitmap.Height.Should().Be(20);
        var pixel = bitmap.GetPixel(10, 10);
        pixel.Red.Should().Be(pixel.Green);
        pixel.Green.Should().Be(pixel.Blue);

        // The PDF rendered from the original image was replaced by the
        // version's, and deleted rather than left in storage.
        using var scope = _fixture.Services.CreateScope();
        var stored = await scope.ServiceProvider
            .GetRequiredService<NeoPrint.Application.Files.IFileRepository>()
            .FindByIdAsync(id);

        using var direct = _fixture.CreateDirectStorageClient();
        var listing = await direct.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = NeoPrintApiFixture.FilesBucket,
            Prefix = $"files/{stored!.UserId:N}/{id:N}/",
        });

        listing.S3Objects.Select(o => o.Key[(o.Key.LastIndexOf('/') + 1)..])
            .Should().BeEquivalentTo("original.png", "v1.png", "print-v1.pdf");
    }

    [DockerFact]
    public async Task A_text_edit_is_saved_as_a_new_version()
    {
        using var user = await SignedInAsync();

        var upload = await user.PostAsync(
            "/api/files", Upload(Encoding.UTF8.GetBytes("Köhnə mətn\n"), "qeyd.txt", "text/plain"));
        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Text becomes a PDF through LibreOffice, which only the backend image
        // has. Once conversion has settled either way, the file is marked
        // ready here, so that the edit is what is tested.
        if (await ConvertAsync(id) != NeoPrint.Domain.Files.FileStatus.Ready)
        {
            using var scope = _fixture.Services.CreateScope();
            var files = scope.ServiceProvider.GetRequiredService<NeoPrint.Application.Files.IFileRepository>();
            var file = await files.FindByIdAsync(id);
            var actor = NeoPrint.Domain.Common.ActorContext.System("test");

            file!.BeginConversion(actor, DateTime.UtcNow);
            file.Complete(file.Original, 1, actor, DateTime.UtcNow);
            await files.UpdateAsync(file);
        }

        var edit = await user.PostAsJsonAsync($"/api/files/{id}/edit", new { text = "Yeni mətn, düzəldilib.\n" });
        edit.StatusCode.Should().Be(HttpStatusCode.OK, await edit.Content.ReadAsStringAsync());

        Encoding.UTF8.GetString(await user.GetByteArrayAsync($"/api/files/{id}/versions/1/content"))
            .Should().Be("Yeni mətn, düzəldilib.\n");
        Encoding.UTF8.GetString(await user.GetByteArrayAsync($"/api/files/{id}/content"))
            .Should().Be("Köhnə mətn\n", "the original is never changed");
    }

    [DockerFact]
    public async Task Edits_that_cannot_apply_are_refused()
    {
        using var user = await SignedInAsync();

        // A PostScript file: a page range at print time only.
        var ps = await user.PostAsync(
            "/api/files", Upload(Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\n%%Pages: 1\nshowpage\n"), "doc.ps"));
        ps.StatusCode.Should().Be(HttpStatusCode.Created, await ps.Content.ReadAsStringAsync());
        var psId = (await ps.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var refused = await user.PostAsJsonAsync($"/api/files/{psId}/edit", new { pages = new[] { new { page = 1 } } });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("PS, PCL");

        // Two kinds at once, and a page given twice.
        (await user.PostAsJsonAsync($"/api/files/{psId}/edit", new { text = "x", image = new { rotate = 90 } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await user.PostAsJsonAsync(
                $"/api/files/{psId}/edit", new { pages = new[] { new { page = 1 }, new { page = 1 } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
