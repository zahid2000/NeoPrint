using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using NeoPrint.Infrastructure.Seeding;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The paper counter (plan section 2.15; step 3 DoD "Kağız sayğacı düzgün
/// azalır; kağız çatmayanda sifariş bloklanır"; docs/TASKS.md T-067).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PaperCounterTests
{
    private readonly NeoPrintApiFixture _fixture;

    public PaperCounterTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static async Task<HttpResponseMessage> RefillAsync(HttpClient admin, Guid kioskId, string mode, int sheets, string paper = "A4")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/kiosks/{kioskId}/paper/refill")
        {
            Content = JsonContent.Create(new { paper, mode, sheets }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await admin.SendAsync(request);
    }

    private static async Task<Guid> PlaceAsync(HttpClient kiosk, int pages, int copies = 1, bool duplex = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-jobs")
        {
            Content = JsonContent.Create(new
            {
                localFileName = "hesabat.pdf",
                pageCount = pages,
                options = new { copies, pageRange = (string?)null, color = false, duplex, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit" },
                dev = true,
            }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await kiosk.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, raw);

        return JsonDocument.Parse(raw).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task ReportAsync(HttpClient kiosk, Guid jobId, string status, int sides, int sheets, string? errorCode = null)
    {
        var response = await kiosk.PostAsJsonAsync(
            $"/api/print-jobs/{jobId}/status", new { status, printedSides = sides, printedSheets = sheets, errorCode });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> SuppliesAsync(HttpClient admin, Guid kioskId)
        => await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kioskId}/supplies");

    private static int A4(JsonElement supplies) => supplies.GetProperty("paper").GetProperty("A4").GetProperty("current").GetInt32();

    [DockerFact]
    public async Task A_filled_tray_goes_down_by_what_each_job_printed_once_however_often_it_is_reported()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"paper-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        (await RefillAsync(admin, kiosk.KioskId, "Full", 0)).StatusCode.Should().Be(HttpStatusCode.OK);
        A4(await SuppliesAsync(admin, kiosk.KioskId)).Should().Be(500);

        // Three pages, two copies, both sides: four sheets.
        var job = await PlaceAsync(kiosk.Client, pages: 3, copies: 2, duplex: true);
        await ReportAsync(kiosk.Client, job, "Printing", 0, 0);
        await ReportAsync(kiosk.Client, job, "Completed", 6, 4);
        await ReportAsync(kiosk.Client, job, "Completed", 6, 4);

        var supplies = await SuppliesAsync(admin, kiosk.KioskId);
        A4(supplies).Should().Be(496, "a report sent again does not count again");

        var printed = supplies.GetProperty("events").EnumerateArray()
            .Where(movement => movement.GetProperty("type").GetString() == "JobPrinted").ToList();
        printed.Should().ContainSingle();
        printed[0].GetProperty("delta").GetInt32().Should().Be(-4);
        printed[0].GetProperty("jobId").GetGuid().Should().Be(job);

        // The kiosk's own view of the tray follows, for its order check.
        var config = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/config");
        config.GetProperty("paperStock").GetProperty("A4").GetProperty("current").GetInt32().Should().Be(496);
    }

    [DockerFact]
    public async Task A_job_stopped_part_way_takes_off_what_came_out()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"paper-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        await RefillAsync(admin, kiosk.KioskId, "Exact", 100);

        var job = await PlaceAsync(kiosk.Client, pages: 10);
        await ReportAsync(kiosk.Client, job, "Printing", 0, 0);
        await ReportAsync(kiosk.Client, job, "Interrupted", 3, 3, "printer.paperJam");

        A4(await SuppliesAsync(admin, kiosk.KioskId)).Should().Be(97);
    }

    [DockerFact]
    public async Task A_refill_adds_sets_or_fills_and_what_does_not_fit_is_refused()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"paper-{Guid.NewGuid():N}"[..20]);
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);

        await RefillAsync(admin, kiosk.KioskId, "Exact", 120);
        (await RefillAsync(admin, kiosk.KioskId, "Add", 200)).StatusCode.Should().Be(HttpStatusCode.OK);
        A4(await SuppliesAsync(admin, kiosk.KioskId)).Should().Be(320);

        var tooMany = await RefillAsync(admin, kiosk.KioskId, "Add", 400);
        tooMany.StatusCode.Should().Be(HttpStatusCode.Conflict, "a business rule, as everywhere in the API");
        (await tooMany.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("kiosks.paperRefillInvalid");

        A4(await SuppliesAsync(admin, kiosk.KioskId)).Should().Be(320);
    }

    [DockerFact]
    public async Task Only_an_account_with_paper_refill_may_refill()
    {
        if (Skip) { return; }

        using var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"paper-{Guid.NewGuid():N}"[..20]);
        var (email, _, _) = DevelopmentSeeder.TestAdmins.First(account => account.Role == "Maliyyə");

        using var finance = _fixture.CreateApiClient();
        var login = await finance.PostAsJsonAsync("/api/auth/admin/login", new { email, password = NeoPrintApiFixture.DevTestPassword });
        finance.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        (await RefillAsync(finance, kiosk.KioskId, "Full", 0)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
