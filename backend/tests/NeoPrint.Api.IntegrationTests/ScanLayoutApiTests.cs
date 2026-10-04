using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Amazon.S3.Model;

using Microsoft.Extensions.DependencyInjection;

using SkiaSharp;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Scans and page layouts (plan section 2.11, flow 9.7; docs/DECISIONS.md
/// 2026-10-05): a scan is saved once as a finished PDF and never edited; a
/// layout keeps its own pictures and can be opened and changed again.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ScanLayoutApiTests
{
    /// <summary>Front and back of an ID card, one above the other, centred on A4 (the template's own numbers).</summary>
    private const string IdFrontBack = """
        {"version":1,"paper":"A4","orientation":"Portrait","marginMm":10,"pages":[{"elements":[
          {"image":"front.jpg","xMm":62.2,"yMm":89.52,"widthMm":85.6,"heightMm":53.98},
          {"image":"back.jpg","xMm":62.2,"yMm":153.5,"widthMm":85.6,"heightMm":53.98}]}]}
        """;

    private readonly NeoPrintApiFixture _fixture;

    public ScanLayoutApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    // -----------------------------------------------------------------------
    // Scans
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_scan_is_refused_until_the_scan_notice_is_accepted()
    {
        using var user = await SignedInAsync();

        var pending = await user.GetFromJsonAsync<JsonElement>("/api/consents/pending?scan=true");
        pending.EnumerateArray().Select(item => item.GetProperty("docType").GetString())
            .Should().Contain("ScanNotice", "the app asks for it before the first scan");

        // Not asked at sign-in: only with ?scan=true.
        (await user.GetFromJsonAsync<JsonElement>("/api/consents/pending")).EnumerateArray()
            .Select(item => item.GetProperty("docType").GetString()).Should().NotContain("ScanNotice");

        var refused = await user.PostAsync("/api/files/scan", ScanForm());

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("skanı barədə məlumatı");

        await AcceptScanNoticeAsync(user);

        var accepted = await user.PostAsync("/api/files/scan", ScanForm());
        accepted.StatusCode.Should().Be(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());

        // Other tests publish new Privacy / Terms versions in the shared
        // database, so only the scan notice is looked at here.
        (await user.GetFromJsonAsync<JsonElement>("/api/consents/pending?scan=true")).EnumerateArray()
            .Select(item => item.GetProperty("docType").GetString()).Should().NotContain("ScanNotice");
    }

    [PdfiumFact]
    public async Task An_id_card_scan_is_a_sensitive_locked_pdf_on_one_a4_page()
    {
        using var user = await SignedInAsync();
        await AcceptScanNoticeAsync(user);

        // isSensitive is not sent: an identity card is personal whatever the
        // phone says (plan section 2.11, the preset table).
        var response = await user.PostAsync("/api/files/scan", ScanForm(sensitive: false));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        var scan = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = scan.GetProperty("id").GetGuid();

        scan.GetProperty("source").GetString().Should().Be("Scan");
        scan.GetProperty("extension").GetString().Should().Be("pdf");
        scan.GetProperty("isSensitive").GetBoolean().Should().BeTrue();
        scan.GetProperty("editLocked").GetBoolean().Should().BeTrue();
        scan.GetProperty("autoDelete").GetString().Should().Be("AfterPrint");
        scan.GetProperty("documentType").GetString().Should().Be("IdCard");
        scan.GetProperty("physicalWidthMm").GetDouble().Should().Be(85.6);
        scan.GetProperty("physicalHeightMm").GetDouble().Should().Be(53.98);

        (await ConvertAsync(id)).Should().Be(Domain.Files.FileStatus.Ready);

        var pdf = await user.GetByteArrayAsync($"/api/files/{id}/pdf");
        using (var document = Pdfium.PdfDocument.Load(pdf))
        {
            document.PageCount.Should().Be(1, "front and back are on one sheet (flow 9.7)");
            var (width, height) = document.PageSize(0);
            (width * 25.4 / 72).Should().BeApproximately(210, 0.1);
            (height * 25.4 / 72).Should().BeApproximately(297, 0.1);
        }

        // Saved means finished: no edit, and not a picture for another layout.
        var edit = await user.PostAsJsonAsync($"/api/files/{id}/edit", new { pages = new[] { new { page = 1, rotate = 90 } } });
        edit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await edit.Content.ReadAsStringAsync()).Should().Contain("dəyişdirilmir");

        var layout = await user.PostAsJsonAsync("/api/files/layout", new { layout = Single($"file:{id}:1") });
        layout.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await layout.Content.ReadAsStringAsync()).Should().Contain("saxlanılmış skandır");

        // Nothing but the PDF is kept: the phone's pictures are not stored.
        using var direct = _fixture.CreateDirectStorageClient();
        var listing = await direct.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = NeoPrintApiFixture.FilesBucket,
            Prefix = "files/",
        });

        (listing.S3Objects ?? []).Where(o => o.Key.Contains(id.ToString("N"), StringComparison.Ordinal))
            .Select(o => o.Key.Split('/').Last())
            .Should().BeEquivalentTo(["original.pdf"]);
    }

    [DockerFact]
    public async Task A_scan_names_only_pictures_it_was_sent_and_only_pictures()
    {
        using var user = await SignedInAsync();
        await AcceptScanNoticeAsync(user);

        var missing = ScanForm(includeBack: false);
        var notPicture = ScanForm();
        var text = new ByteArrayContent("not an image at all"u8.ToArray());
        text.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        notPicture.Add(text, "images", "notes.jpg");

        var custom = ScanForm(documentType: "Custom");

        (await ErrorAsync(user, missing)).Should().Contain("back.jpg").And.Contain("göndərilmədi");
        (await ErrorAsync(user, notPicture)).Should().Contain("notes.jpg").And.Contain("şəkil deyil");
        (await ErrorAsync(user, custom)).Should().Contain("1 ilə 1000 mm");
    }

    // -----------------------------------------------------------------------
    // Layouts
    // -----------------------------------------------------------------------

    [PdfiumFact]
    public async Task A_layout_keeps_its_own_pictures_and_opens_again_after_the_source_is_gone()
    {
        using var user = await SignedInAsync();
        var photo = await UploadReadyAsync(user, Jpeg(800, 1066, SKColors.SteelBlue), "foto.jpg", "image/jpeg");
        var pdf = await UploadReadyAsync(user, ConversionTests.OnePagePdf(2), "sənəd.pdf", "application/pdf");

        var created = await user.PostAsJsonAsync("/api/files/layout", new
        {
            fileName = "Fotolar",
            layout = Layout(
                ("file:" + photo, 20, 20, 30, 40),
                ($"file:{pdf}:2", 60, 20, 105, 148.5)),
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var file = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = file.GetProperty("id").GetGuid();

        file.GetProperty("source").GetString().Should().Be("Layout");
        file.GetProperty("fileName").GetString().Should().Be("Fotolar.pdf");
        file.GetProperty("hasLayout").GetBoolean().Should().BeTrue();
        file.GetProperty("editLocked").GetBoolean().Should().BeFalse();
        await ConvertAsync(id);

        // The source photo goes; the layout does not notice.
        (await user.DeleteAsync($"/api/files/{photo}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var opened = await user.GetFromJsonAsync<JsonElement>($"/api/files/{id}/layout");
        var assets = opened.GetProperty("assets").EnumerateArray().ToList();
        assets.Should().HaveCount(2);
        assets.Select(asset => asset.GetProperty("mediaType").GetString()).Should().BeEquivalentTo(["image/jpeg", "image/png"]);

        var names = opened.GetProperty("layout").GetProperty("pages")[0].GetProperty("elements").EnumerateArray()
            .Select(element => element.GetProperty("image").GetString()!).ToList();
        names.Should().OnlyContain(name => name.StartsWith("asset:", StringComparison.Ordinal));

        var asset = await user.GetAsync($"/api/files/{id}/layout/assets/{names[0]["asset:".Length..]}");
        asset.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var bitmap = SKBitmap.Decode(await asset.Content.ReadAsByteArrayAsync()))
        {
            bitmap.Width.Should().Be(800, "the layout kept the photo as it was");
        }

        // Change it: keep the photo, moved and turned; drop the page.
        var changed = await user.PutAsJsonAsync($"/api/files/{id}/layout", new
        {
            layout = Layout((names[0], 40, 40, 30, 40, 90)),
        });

        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
        await ConvertAsync(id);

        var after = await user.GetFromJsonAsync<JsonElement>($"/api/files/{id}");
        after.GetProperty("versionCount").GetInt32().Should().Be(1);
        after.GetProperty("status").GetString().Should().Be("Ready");

        var reopened = await user.GetFromJsonAsync<JsonElement>($"/api/files/{id}/layout");
        reopened.GetProperty("assets").GetArrayLength().Should().Be(1, "the page no longer used was deleted");
        reopened.GetProperty("layout").GetProperty("pages")[0].GetProperty("elements")[0]
            .GetProperty("rotationDeg").GetDouble().Should().Be(90);

        // A layout is changed in its own editor, not page by page.
        var pageEdit = await user.PostAsJsonAsync($"/api/files/{id}/edit", new { pages = new[] { new { page = 1, rotate = 90 } } });
        pageEdit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await pageEdit.Content.ReadAsStringAsync()).Should().Contain("Tərtibatı redaktə et");

        // Deleting the layout deletes its pictures as well.
        (await user.DeleteAsync($"/api/files/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var direct = _fixture.CreateDirectStorageClient();
        var listing = await direct.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = NeoPrintApiFixture.FilesBucket,
            Prefix = "files/",
        });

        (listing.S3Objects ?? []).Should().NotContain(o => o.Key.Contains(id.ToString("N"), StringComparison.Ordinal));
    }

    [PdfiumFact]
    public async Task A_layout_with_a_personal_document_is_personal_too()
    {
        using var user = await SignedInAsync();

        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Jpeg(400, 252, SKColors.Black));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(part, "file", "vəsiqə.jpg");
        form.Add(new StringContent("true"), "isSensitive");

        var upload = await user.PostAsync("/api/files", form);
        var card = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await ConvertAsync(card);

        var created = await user.PostAsJsonAsync("/api/files/layout", new { layout = Single("file:" + card) });

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var layout = await created.Content.ReadFromJsonAsync<JsonElement>();
        layout.GetProperty("isSensitive").GetBoolean().Should().BeTrue();
        layout.GetProperty("autoDelete").GetString().Should().Be("AfterPrint");
    }

    [DockerFact]
    public async Task A_layout_naming_someone_elses_file_or_nonsense_is_refused()
    {
        using var owner = await SignedInAsync();
        using var stranger = await SignedInAsync();
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Jpeg(100, 100, SKColors.Red));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(part, "file", "a.jpg");
        var theirs = (await (await owner.PostAsync("/api/files", form)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        (await stranger.PostAsJsonAsync("/api/files/layout", new { layout = Single("file:" + theirs) }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "somebody else's file is not found, not forbidden");

        var nonsense = await stranger.PostAsJsonAsync("/api/files/layout", new { layout = Single("http://example.com/x.jpg") });
        nonsense.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var offPage = await stranger.PostAsJsonAsync("/api/files/layout", new
        {
            layout = Layout(("file:" + Guid.NewGuid(), 400, 20, 30, 40)),
        });
        offPage.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await offPage.Content.ReadAsStringAsync()).Should().Contain("kağızdan kənardadır");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _fixture.CreateApiClient();
        var email = $"scan-{Guid.NewGuid():N}@neoprint.test";

        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "ScanPassword!2026",
            fullName = "Scan Tester",
            language = "az",
            deviceId = Guid.NewGuid().ToString("N"),
            consents = new[]
            {
                new { docType = "Privacy", version = "0.1-draft" },
                new { docType = "Terms", version = "0.1-draft" },
            },
        });

        register.StatusCode.Should().Be(HttpStatusCode.OK, await register.Content.ReadAsStringAsync());

        var confirm = await client.PostAsJsonAsync(
            "/api/auth/confirm-email",
            new { email, code = _fixture.Emails.CodeFor(email), deviceId = Guid.NewGuid().ToString("N") });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());

        var token = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return client;
    }

    private static async Task AcceptScanNoticeAsync(HttpClient user)
    {
        var notice = await user.GetFromJsonAsync<JsonElement>("/api/legal/ScanNotice");

        var accept = new HttpRequestMessage(HttpMethod.Post, "/api/consents")
        {
            Content = JsonContent.Create(new
            {
                consents = new[] { new { docType = "ScanNotice", version = notice.GetProperty("version").GetString() } },
                deviceId = "scan-test-device",
            }),
        };
        accept.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await user.SendAsync(accept);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static MultipartFormDataContent ScanForm(
        bool sensitive = true, bool includeBack = true, string documentType = "IdCard")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(IdFrontBack), "layout" },
            { new StringContent(documentType), "documentType" },
            { new StringContent(sensitive ? "true" : "false"), "isSensitive" },
            { new StringContent("Vəsiqə"), "fileName" },
        };

        foreach (var (name, color) in includeBack
                     ? new[] { ("front.jpg", SKColors.DarkSlateBlue), ("back.jpg", SKColors.DarkOliveGreen) }
                     : [("front.jpg", SKColors.DarkSlateBlue)])
        {
            var part = new ByteArrayContent(Jpeg(1011, 638, color));
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(part, "images", name);
        }

        return form;
    }

    private static async Task<string> ErrorAsync(HttpClient user, MultipartFormDataContent form)
    {
        var response = await user.PostAsync("/api/files/scan", form);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        return await response.Content.ReadAsStringAsync();
    }

    private static object Single(string image) => Layout((image, 20, 20, 85.6, 53.98));

    private static object Layout(params (string Image, double X, double Y, double W, double H)[] elements)
        => Layout(elements.Select(e => (e.Image, e.X, e.Y, e.W, e.H, 0.0)).ToArray());

    private static object Layout(params (string Image, double X, double Y, double W, double H, double Rotation)[] elements) => new
    {
        version = 1,
        paper = "A4",
        orientation = "Portrait",
        marginMm = 10,
        pages = new[]
        {
            new
            {
                elements = elements.Select(e => new
                {
                    image = e.Image,
                    xMm = e.X,
                    yMm = e.Y,
                    widthMm = e.W,
                    heightMm = e.H,
                    rotationDeg = e.Rotation,
                }).ToArray(),
            },
        },
    };

    private static byte[] Jpeg(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);

        return data.ToArray();
    }

    private async Task<Guid> UploadReadyAsync(HttpClient user, byte[] content, string name, string mediaType)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(part, "file", name);

        var upload = await user.PostAsync("/api/files", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());

        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await ConvertAsync(id)).Should().Be(Domain.Files.FileStatus.Ready);

        return id;
    }

    /// <summary>Waits until the file is Ready or Failed, converting it here if the bus has not yet.</summary>
    private async Task<Domain.Files.FileStatus> ConvertAsync(Guid id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            using var scope = _fixture.Services.CreateScope();
            var file = await scope.ServiceProvider.GetRequiredService<NeoPrint.Application.Files.IFileRepository>().FindByIdAsync(id);

            if (file!.Status is Domain.Files.FileStatus.Ready or Domain.Files.FileStatus.Failed)
            {
                return file.Status;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"File {id} is still {file.Status}.");
            }

            try
            {
                await scope.ServiceProvider.GetRequiredService<NeoPrint.Infrastructure.Conversion.DocumentConversionService>().ConvertAsync(id);
            }
            catch (Domain.Common.DomainException)
            {
                await Task.Delay(200);
            }
        }
    }
}
