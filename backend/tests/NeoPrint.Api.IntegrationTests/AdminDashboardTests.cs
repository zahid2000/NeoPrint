using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using NeoPrint.Infrastructure.Seeding;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The dashboard's step 2 cards: files, conversion failures, storage health and
/// sync (plan sections 6, 8A.3).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminDashboardTests
{
    private readonly NeoPrintApiFixture _fixture;

    public AdminDashboardTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private async Task<HttpClient> AdminAsync(string email)
    {
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync(
            "/api/auth/admin/login", new { email, password = NeoPrintApiFixture.DevTestPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }

    private async Task<HttpClient> MobileUserAsync()
    {
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = DevelopmentSeeder.TestUserEmail,
            password = NeoPrintApiFixture.DevTestPassword,
            deviceId = Guid.NewGuid().ToString("N"),
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }

    [DockerFact]
    public async Task Storage_is_shown_to_those_who_manage_it_without_its_secrets()
    {
        using var superAdmin = await AdminAsync(DevelopmentSeeder.TestAdmins[0].Email);

        var dashboard = await superAdmin.GetFromJsonAsync<JsonElement>("/api/admin/dashboard");

        var providers = dashboard.GetProperty("storage").GetProperty("providers");
        providers.GetArrayLength().Should().BeGreaterThan(0);

        var primary = providers.EnumerateArray().Single(p => p.GetProperty("role").GetString() == "Primary");
        primary.GetProperty("healthStatus").GetString().Should().NotBeNullOrEmpty();

        // The dashboard is seen by more people than the storage page: no
        // endpoint, no key hint, no bucket names.
        primary.TryGetProperty("accessKeyHint", out _).Should().BeFalse();
        primary.TryGetProperty("endpoint", out _).Should().BeFalse();
        primary.TryGetProperty("buckets", out _).Should().BeFalse();
    }

    [DockerFact]
    public async Task An_admin_without_storage_access_gets_the_files_but_not_the_storage()
    {
        // The Admin template has dashboard.view and deliberately no storage.manage.
        using var admin = await AdminAsync(DevelopmentSeeder.TestAdmins[1].Email);

        var dashboard = await admin.GetFromJsonAsync<JsonElement>("/api/admin/dashboard");

        dashboard.TryGetProperty("storage", out _).Should().BeFalse();
        dashboard.GetProperty("files").GetProperty("count").GetInt64().Should().BeGreaterThanOrEqualTo(0);
    }

    [PdfiumFact]
    public async Task A_failed_conversion_is_counted_and_an_upload_adds_to_the_totals()
    {
        using var superAdmin = await AdminAsync(DevelopmentSeeder.TestAdmins[0].Email);
        using var user = await MobileUserAsync();

        var before = (await superAdmin.GetFromJsonAsync<JsonElement>("/api/admin/dashboard")).GetProperty("files");

        // Passes the signature check, then fails in PDFium: a conversion failure.
        var corrupt = Encoding.ASCII.GetBytes("%PDF-1.7\nnot really a pdf at all\n");
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(corrupt);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, "file", "broken.pdf");

        var upload = await user.PostAsync("/api/files", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());
        var id = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        string? status;
        do
        {
            await Task.Delay(250);
            status = (await user.GetFromJsonAsync<JsonElement>($"/api/files/{id}")).GetProperty("status").GetString();
        }
        while (status is "Pending" or "Converting" && DateTime.UtcNow < deadline);

        status.Should().Be("Failed");

        var after = (await superAdmin.GetFromJsonAsync<JsonElement>("/api/admin/dashboard")).GetProperty("files");

        after.GetProperty("count").GetInt64().Should().Be(before.GetProperty("count").GetInt64() + 1);
        after.GetProperty("totalBytes").GetInt64()
            .Should().Be(before.GetProperty("totalBytes").GetInt64() + corrupt.Length);
        after.GetProperty("failedLast24Hours").GetInt64()
            .Should().Be(before.GetProperty("failedLast24Hours").GetInt64() + 1);
        after.GetProperty("failedLast7Days").GetInt64()
            .Should().Be(before.GetProperty("failedLast7Days").GetInt64() + 1);
    }
}
