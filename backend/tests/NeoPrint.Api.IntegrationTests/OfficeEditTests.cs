using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NeoPrint.Application.Office;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The Office editor's session and the WOPI host Collabora reads and saves
/// through (plan section 2.2, Addım 9; T-459). Collabora itself is stood in
/// for by its discovery: what is tested is the API's side of the contract.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OfficeEditTests
{
    private const string EditorPath = "/browser/0123abcd/cool.html?";

    private readonly NeoPrintApiFixture _fixture;

    public OfficeEditTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task The_office_editor_is_absent_while_its_flag_is_off()
    {
        using var user = await SignedInAsync();
        var id = await UploadReadyDocxAsync(user);

        var response = await user.PostAsync($"/api/files/{id}/office-session", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the flag is seeded off: Collabora may not be running");
    }

    [DockerFact]
    public async Task Without_collabora_the_editor_says_it_is_unavailable()
    {
        using var admin = await SuperAdminAsync();
        await SetFlagAsync(admin, on: true);

        try
        {
            await using var host = HostWith(new FakeDiscovery(path: null));
            using var user = await SignedInAsync(host);
            var id = await UploadReadyDocxAsync(user);

            var response = await user.PostAsync($"/api/files/{id}/office-session", null);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString().Should().Be("office.unavailable");
        }
        finally
        {
            await SetFlagAsync(admin, on: false);
        }
    }

    [DockerFact]
    public async Task A_save_in_the_editor_is_a_new_version_in_the_same_format()
    {
        using var admin = await SuperAdminAsync();
        await SetFlagAsync(admin, on: true);

        try
        {
            await using var host = HostWith(new FakeDiscovery(EditorPath));
            using var user = await SignedInAsync(host);
            var id = await UploadReadyDocxAsync(user);

            var start = await user.PostAsync($"/api/files/{id}/office-session", null);
            start.StatusCode.Should().Be(HttpStatusCode.OK, await start.Content.ReadAsStringAsync());
            start.Headers.CacheControl!.NoStore.Should().BeTrue();

            var session = await start.Content.ReadFromJsonAsync<JsonElement>();
            var token = session.GetProperty("accessToken").GetString()!;
            var editor = session.GetProperty("editorPath").GetString()!;

            // The page on this origin; the token is never in its address.
            editor.Should().StartWith(EditorPath + "WOPISrc=");
            editor.Should().Contain(Uri.EscapeDataString($"/wopi/files/{id:N}"));
            editor.Should().NotContain(token);
            session.GetProperty("accessTokenTtl").GetInt64()
                .Should().BeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(29).ToUnixTimeMilliseconds());

            using var wopi = host.CreateClient();
            var query = $"access_token={Uri.EscapeDataString(token)}";

            // CheckFileInfo, in WOPI's own names.
            var info = await wopi.GetFromJsonAsync<JsonElement>($"/wopi/files/{id:N}?{query}");
            info.GetProperty("BaseFileName").GetString().Should().Be("report.docx");
            info.GetProperty("UserCanWrite").GetBoolean().Should().BeTrue();
            info.GetProperty("SupportsLocks").GetBoolean().Should().BeFalse();
            info.GetProperty("DisablePrint").GetBoolean().Should().BeTrue("printing is paid for at a kiosk");
            info.GetProperty("Version").GetString().Should().Be("0");

            // GetFile: the document as uploaded.
            var original = await wopi.GetByteArrayAsync($"/wopi/files/{id:N}/contents?{query}");
            original.Should().Equal(Docx("Original text"));

            // PutFile without the override is not a save.
            (await wopi.PostAsync($"/wopi/files/{id:N}/contents?{query}", new ByteArrayContent(Docx("x"))))
                .StatusCode.Should().Be(HttpStatusCode.NotImplemented);

            var edited = Docx("Edited in Collabora");
            using (var put = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{id:N}/contents?{query}")
                   {
                       Content = new ByteArrayContent(edited),
                   })
            {
                put.Headers.Add("X-WOPI-Override", "PUT");
                var saved = await wopi.SendAsync(put);
                saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
            }

            using (var scope = _fixture.Services.CreateScope())
            {
                var file = await scope.ServiceProvider
                    .GetRequiredService<NeoPrint.Application.Files.IFileRepository>()
                    .FindByIdAsync(id);

                file!.LatestVersion!.Number.Should().Be(1);
                file.CurrentExtension.Should().Be("docx", "a save keeps the document's own format");
            }

            // Collabora reads the saved document back, and sees it is newer.
            (await wopi.GetByteArrayAsync($"/wopi/files/{id:N}/contents?{query}")).Should().Equal(edited);
            (await wopi.GetFromJsonAsync<JsonElement>($"/wopi/files/{id:N}?{query}"))
                .GetProperty("Version").GetString().Should().Be("1");

            var versions = await user.GetFromJsonAsync<JsonElement>($"/api/files/{id}/versions");
            versions.GetArrayLength().Should().Be(1);
        }
        finally
        {
            await SetFlagAsync(admin, on: false);
        }
    }

    [DockerFact]
    public async Task A_wopi_call_without_the_sessions_token_opens_nothing()
    {
        using var admin = await SuperAdminAsync();
        await SetFlagAsync(admin, on: true);

        try
        {
            await using var host = HostWith(new FakeDiscovery(EditorPath));
            using var user = await SignedInAsync(host);
            var id = await UploadReadyDocxAsync(user);
            var other = await UploadReadyDocxAsync(user);

            var token = (await (await user.PostAsync($"/api/files/{id}/office-session", null))
                    .Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("accessToken").GetString()!;

            using var wopi = host.CreateClient();

            (await wopi.GetAsync($"/wopi/files/{id:N}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await wopi.GetAsync($"/wopi/files/{id:N}?access_token=not-a-token"))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await wopi.GetAsync($"/wopi/files/{id:N}/contents?access_token=not-a-token"))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // A token opens its own file only, even one of the same owner's.
            (await wopi.GetAsync($"/wopi/files/{other:N}?access_token={Uri.EscapeDataString(token)}"))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using var put = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{other:N}/contents?access_token={Uri.EscapeDataString(token)}")
            {
                Content = new ByteArrayContent(Docx("Overwritten")),
            };
            put.Headers.Add("X-WOPI-Override", "PUT");
            (await wopi.SendAsync(put)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Locks and the rest are not offered.
            using var lockRequest = new HttpRequestMessage(HttpMethod.Post, $"/wopi/files/{id:N}?access_token={Uri.EscapeDataString(token)}");
            lockRequest.Headers.Add("X-WOPI-Override", "LOCK");
            (await wopi.SendAsync(lockRequest)).StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        }
        finally
        {
            await SetFlagAsync(admin, on: false);
        }
    }

    [DockerFact]
    public async Task A_stranger_cannot_open_someone_elses_file_in_the_editor()
    {
        using var admin = await SuperAdminAsync();
        await SetFlagAsync(admin, on: true);

        try
        {
            await using var host = HostWith(new FakeDiscovery(EditorPath));
            using var owner = await SignedInAsync(host);
            using var stranger = await SignedInAsync(host);
            var id = await UploadReadyDocxAsync(owner);

            (await stranger.PostAsync($"/api/files/{id}/office-session", null))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await SetFlagAsync(admin, on: false);
        }
    }

    [Fact]
    public void Discovery_gives_each_extension_its_edit_page_as_a_path_on_this_origin()
    {
        const string discovery = """
            <wopi-discovery>
              <net-zone name="external-http">
                <app name="writer">
                  <action default="true" ext="docx" name="edit" urlsrc="http://collabora:9980/browser/abc123/cool.html?"/>
                  <action ext="docx" name="view" urlsrc="http://collabora:9980/browser/abc123/cool.html?view"/>
                  <action ext="pdf" name="view" urlsrc="http://collabora:9980/browser/abc123/cool.html?"/>
                </app>
                <app name="calc">
                  <action ext="xlsx" name="edit" urlsrc="http://collabora:9980/browser/abc123/cool.html?"/>
                  <action ext="" name="edit" urlsrc="http://collabora:9980/browser/abc123/cool.html?"/>
                  <action ext="ods" name="edit" urlsrc="not a url"/>
                </app>
              </net-zone>
            </wopi-discovery>
            """;

        var editors = NeoPrint.Infrastructure.Office.CollaboraDiscovery.Parse(discovery);

        editors.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["docx"] = "/browser/abc123/cool.html?",
            ["xlsx"] = "/browser/abc123/cool.html?",
        }, "only edit actions with an extension and an address count, and Collabora's host is dropped");
    }

    // -----------------------------------------------------------------------

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> HostWith(IOfficeDiscovery discovery)
        => _fixture.WithHttpOnlyHost(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IOfficeDiscovery>();
            services.AddSingleton(discovery);
        }));

    private sealed class FakeDiscovery(string? path) : IOfficeDiscovery
    {
        public Task<string?> EditorPathAsync(string extension, CancellationToken cancellationToken)
            => Task.FromResult(extension == "docx" ? path : null);
    }

    /// <summary>A minimal but real Word document.</summary>
    private static byte[] Docx(string text)
    {
        using var memory = new MemoryStream();

        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }

            Add("[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Add("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add("word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
                + $"<w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>");
        }

        return memory.ToArray();
    }

    /// <summary>
    /// Uploads a document and settles it as Ready. The converter needs
    /// LibreOffice, which a developer's machine may not have; what is tested
    /// here is the editor, so a file the converter could not convert is
    /// marked converted by hand.
    /// </summary>
    private async Task<Guid> UploadReadyDocxAsync(HttpClient user)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Docx("Original text"));
        part.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        form.Add(part, "file", "report.docx");

        var upload = await user.PostAsync("/api/files", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            using var scope = _fixture.Services.CreateScope();
            var files = scope.ServiceProvider.GetRequiredService<NeoPrint.Application.Files.IFileRepository>();
            var file = await files.FindByIdAsync(id);

            if (file!.Status == FileStatus.Ready)
            {
                return id;
            }

            // Failed, or still waiting for a converter after a few seconds.
            if (file.Status == FileStatus.Failed
                || (file.Status == FileStatus.Pending && DateTime.UtcNow > deadline.AddSeconds(-25)))
            {
                try
                {
                    var now = DateTime.UtcNow;
                    file.BeginConversion(ActorContext.System("test"), now);
                    file.Complete(file.CurrentContent, 1, ActorContext.System("test"), now);
                    await files.UpdateAsync(file);
                    return id;
                }
                catch (DomainException)
                {
                    // The converter got there first (a transition or the row version): look again.
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"File {id} is still {file.Status}.");
            }

            await Task.Delay(200);
        }
    }

    /// <summary>
    /// Signed in through the fixture's host, whose mail is captured, and
    /// handed to <paramref name="host"/> when the test needs its own.
    /// </summary>
    private async Task<HttpClient> SignedInAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? host = null)
    {
        using var signedIn = await RegisterAsync();
        var client = host?.CreateClient() ?? _fixture.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = signedIn.DefaultRequestHeaders.Authorization;
        return client;
    }

    private async Task<HttpClient> RegisterAsync()
    {
        var client = _fixture.CreateApiClient();
        var email = $"office-{Guid.NewGuid():N}@neoprint.test";

        var register = await client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password = "OfficePassword!2026",
                fullName = "Office Tester",
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

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }

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
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }

    /// <summary>The flags are shared by the whole collection: each test puts this one back off.</summary>
    private static async Task SetFlagAsync(HttpClient admin, bool on)
    {
        var flag = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/features"))
            .EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "office_editor");

        var response = await admin.PutAsJsonAsync("/api/admin/features/office_editor", new
        {
            defaultEnabled = on,
            rolloutPercent = flag.GetProperty("rolloutPercent").GetInt32(),
            kioskOverrides = flag.GetProperty("kioskOverrides").Deserialize<Dictionary<string, bool>>(),
            userAllowlist = flag.GetProperty("userAllowlist").Deserialize<Guid[]>(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
