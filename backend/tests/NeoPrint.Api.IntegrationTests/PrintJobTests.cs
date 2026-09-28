using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.PrintJobs;

using StackExchange.Redis;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Print jobs, the queue and the price snapshot (plan step 3; docs/TASKS.md
/// T-047, T-063).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PrintJobTests
{
    private readonly NeoPrintApiFixture _fixture;

    public PrintJobTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static object Options(
        int copies = 1, string? pageRange = null, bool color = false, bool duplex = false, string paper = "A4", int pagesPerSheet = 1)
        => new { copies, pageRange, color, duplex, paper, orientation = "Auto", pagesPerSheet, scaling = "Fit" };

    private static async Task<HttpResponseMessage> PlaceAsync(
        HttpClient kiosk, int pages, object? options = null, bool dev = true, string? file = "hesabat.pdf")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/print-jobs")
        {
            Content = JsonContent.Create(new { localFileName = file, pageCount = pages, options = options ?? Options(), dev }),
        };

        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await kiosk.SendAsync(request);
    }

    private static async Task<JsonElement> PlacedAsync(HttpClient kiosk, int pages, object? options = null, bool dev = true)
    {
        var response = await PlaceAsync(kiosk, pages, options, dev);
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, raw);

        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static async Task<JsonElement> ReportAsync(
        HttpClient kiosk, Guid jobId, string status, int sides = 0, int sheets = 0, string? errorCode = null)
    {
        var response = await kiosk.PostAsJsonAsync(
            $"/api/print-jobs/{jobId}/status", new { status, printedSides = sides, printedSheets = sheets, errorCode });
        var raw = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    /// <summary>Puts paper in the tray; the refill screen arrives with the paper counter.</summary>
    private async Task LoadPaperAsync(Guid kioskId, int sheets, string paper = "A4")
    {
        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        await context.Kiosks.UpdateOneAsync(
            Builders<Kiosk>.Filter.Eq(kiosk => kiosk.Id, kioskId),
            Builders<Kiosk>.Update.Set($"PaperStock.{paper}.Current", sheets));
    }

    private async Task<TestKiosk> ReadyKioskAsync(string name, int paper = 500, bool a3 = false)
    {
        var kiosk = await TestKiosks.ActivatedAsync(_fixture, $"{name}-{Guid.NewGuid():N}"[..30], a3: a3);
        await LoadPaperAsync(kiosk.KioskId, paper);

        return kiosk;
    }

    [DockerFact]
    public async Task A_flash_order_is_priced_on_the_server_and_queued_in_development()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("queue");

        var job = await PlacedAsync(kiosk.Client, pages: 10);

        job.GetProperty("status").GetString().Should().Be("Queued");
        job.GetProperty("jobCode").GetString().Should().MatchRegex("^[0-9]{6}$");
        job.GetProperty("paymentSkipped").GetBoolean().Should().BeTrue();

        // The seeded default profile: 10 black-and-white sides at 0.10.
        job.GetProperty("price").GetProperty("total").GetDecimal().Should().Be(1.00m);
        job.GetProperty("requiredSheets").GetInt32().Should().Be(10);
        job.GetProperty("queuePosition").GetInt32().Should().Be(1);

        var queue = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue");
        queue.GetArrayLength().Should().Be(1);
        queue[0].GetProperty("jobCode").GetString().Should().Be(job.GetProperty("jobCode").GetString());
        IsAbsent(queue[0], "userInitial").Should().BeTrue("a flash job is a guest's");

        // The Redis sorted set holds it too (plan step 3: queue:{kioskId}).
        var redis = _fixture.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        (await redis.SortedSetRankAsync(RedisPrintQueue.KeyFor(kiosk.KioskId), job.GetProperty("id").GetGuid().ToString("N")))
            .Should().Be(0);
    }

    [DockerFact]
    public async Task The_price_a_job_was_made_with_does_not_follow_the_profile()
    {
        if (Skip) { return; }

        // T-047: plan section 2.3, "sonrakı profil dəyişiklikləri mövcud
        // job-lara təsir etmir" - at the level of a real job.
        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync("snapshot");

        var profile = await admin.PostAsJsonAsync("/api/admin/pricing-profiles", Profile($"Snapshot {Guid.NewGuid():N}", 0.20m));
        profile.StatusCode.Should().Be(HttpStatusCode.OK, await profile.Content.ReadAsStringAsync());
        var profileId = (await profile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await admin.PostAsJsonAsync($"/api/admin/pricing-profiles/{profileId}/assign", new { kioskIds = new[] { kiosk.KioskId } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var job = await PlacedAsync(kiosk.Client, pages: 10);
        var jobId = job.GetProperty("id").GetGuid();
        job.GetProperty("price").GetProperty("total").GetDecimal().Should().Be(2.00m, "10 sides at 0.20");

        // The profile triples its price, and then is deleted outright.
        (await admin.PutAsJsonAsync($"/api/admin/pricing-profiles/{profileId}", Profile($"Snapshot {Guid.NewGuid():N}", 0.60m)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var quote = await admin.PostAsJsonAsync("/api/pricing/quote", new
        {
            kioskId = kiosk.KioskId,
            options = new { pageCount = 10, copies = 1, duplex = false, color = false, paper = "A4", pagesPerSheet = 1 },
        });
        (await quote.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("price").GetProperty("finalPrice").GetDecimal()
            .Should().Be(6.00m, "a new order gets the new price");

        var after = await kiosk.Client.GetFromJsonAsync<JsonElement>($"/api/print-jobs/{jobId}");
        after.GetProperty("price").GetProperty("total").GetDecimal().Should().Be(2.00m, "the job keeps the price it was made with");

        // And the document itself carries the rates, so an audit can recompute it.
        using var scope = _fixture.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IPrintJobRepository>().FindByIdAsync(jobId);
        stored!.Price.Recompute().FinalPrice.Should().Be(2.00m);
        stored.Price.ProfileId.Should().Be(profileId);
    }

    [DockerFact]
    public async Task A_job_goes_through_the_printer_and_leaves_the_queue_with_every_step_recorded()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("flow");
        var job = await PlacedAsync(kiosk.Client, pages: 4, Options(copies: 2, duplex: true));
        var jobId = job.GetProperty("id").GetGuid();

        job.GetProperty("requiredSheets").GetInt32().Should().Be(4, "8 sides on both sides of 4 sheets");

        (await ReportAsync(kiosk.Client, jobId, "Printing")).GetProperty("status").GetString().Should().Be("Printing");

        var done = await ReportAsync(kiosk.Client, jobId, "Completed", sides: 8, sheets: 4);
        done.GetProperty("status").GetString().Should().Be("Completed");
        done.GetProperty("printedSheets").GetInt32().Should().Be(4);
        IsAbsent(done, "queuePosition").Should().BeTrue("a finished job is not in the queue");

        // The same report again - a retry after a lost response - changes nothing.
        (await ReportAsync(kiosk.Client, jobId, "Completed", sides: 8, sheets: 4))
            .GetProperty("printedSheets").GetInt32().Should().Be(4);

        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue")).GetArrayLength().Should().Be(0);

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/print-jobs/{jobId}");

        detail.GetProperty("history").EnumerateArray()
            .Select(step => $"{step.GetProperty("from").GetString()}->{step.GetProperty("to").GetString()}")
            .Should().Equal(
                "Draft->PendingPayment",
                "PendingPayment->Paid",
                "Paid->Queued",
                "Queued->Printing",
                "Printing->Completed");
    }

    [DockerFact]
    public async Task A_printer_that_claims_more_than_the_job_is_not_believed()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("clamp");
        var jobId = (await PlacedAsync(kiosk.Client, pages: 3)).GetProperty("id").GetGuid();

        await ReportAsync(kiosk.Client, jobId, "Printing");
        var done = await ReportAsync(kiosk.Client, jobId, "Completed", sides: 30, sheets: 30);

        done.GetProperty("printedSheets").GetInt32().Should().Be(3, "the paper counter must not follow a wrong report");
    }

    [DockerFact]
    public async Task An_interrupted_job_says_why_in_the_reader_s_language()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("interrupt");
        var jobId = (await PlacedAsync(kiosk.Client, pages: 5)).GetProperty("id").GetGuid();

        await ReportAsync(kiosk.Client, jobId, "Printing");
        var stopped = await ReportAsync(kiosk.Client, jobId, "Interrupted", sides: 2, sheets: 2, errorCode: "printer.outOfPaper");

        stopped.GetProperty("status").GetString().Should().Be("Interrupted");
        stopped.GetProperty("errorReason").GetString().Should().Be("Printerdə kağız bitib.");

        using var english = new HttpRequestMessage(HttpMethod.Get, $"/api/print-jobs/{jobId}");
        english.Headers.Add("Accept-Language", "en");
        var read = await (await kiosk.Client.SendAsync(english)).Content.ReadFromJsonAsync<JsonElement>();
        read.GetProperty("errorReason").GetString().Should().Be("The printer is out of paper.");

        // A code the backend does not know still stops the job, as a plain printer error.
        var other = (await PlacedAsync(kiosk.Client, pages: 1)).GetProperty("id").GetGuid();
        await ReportAsync(kiosk.Client, other, "Printing");
        (await ReportAsync(kiosk.Client, other, "Interrupted", errorCode: "printer.onFire"))
            .GetProperty("errorReason").GetString().Should().Be("Printer xətası.");
    }

    [DockerFact]
    public async Task What_the_printer_cannot_do_is_refused()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("capabilities", a3: false);

        var a3 = await PlaceAsync(kiosk.Client, 2, Options(paper: "A3"));
        a3.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await a3.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("printJobs.a3Unavailable");

        var range = await PlaceAsync(kiosk.Client, 5, Options(pageRange: "2-9"));
        range.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await range.Content.ReadAsStringAsync()).Should().Contain("printJobs.pageRangeInvalid");

        var pages = await PlaceAsync(kiosk.Client, 201);
        pages.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await pages.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("printJobs.tooManyPages");

        var copies = await PlaceAsync(kiosk.Client, 1, Options(copies: 51));
        copies.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task A_page_range_prices_only_the_pages_it_selects()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("range");
        var job = await PlacedAsync(kiosk.Client, 10, Options(pageRange: "1-3,5,8-10"));

        job.GetProperty("selectedPages").GetInt32().Should().Be(7);
        job.GetProperty("price").GetProperty("total").GetDecimal().Should().Be(0.70m);
    }

    [DockerFact]
    public async Task An_order_the_paper_cannot_cover_is_refused_counting_what_is_already_promised()
    {
        if (Skip) { return; }

        // Plan section 2.15: Paper.BlockWhenInsufficient is on by default.
        using var kiosk = await ReadyKioskAsync("paper", paper: 10);

        await PlacedAsync(kiosk.Client, 6);

        // Ten sheets in the tray, six promised to the queued job: four left.
        var refused = await PlaceAsync(kiosk.Client, 6);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("printJobs.notEnoughPaper");

        (await PlaceAsync(kiosk.Client, 4)).StatusCode.Should().Be(HttpStatusCode.Created, "four still fit");
    }

    [DockerFact]
    public async Task A_queued_job_is_cancelled_by_its_code_and_one_at_the_printer_is_not()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("cancel");
        var first = await PlacedAsync(kiosk.Client, 2);
        var second = await PlacedAsync(kiosk.Client, 2);

        var found = await kiosk.Client.GetFromJsonAsync<JsonElement>($"/api/print-jobs/by-code/{second.GetProperty("jobCode").GetString()}");
        found.GetProperty("queuePosition").GetInt32().Should().Be(2);

        var cancelled = await CancelAsync(kiosk.Client, second.GetProperty("id").GetGuid());
        cancelled.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Cancelled");

        await ReportAsync(kiosk.Client, first.GetProperty("id").GetGuid(), "Printing");
        var refused = await CancelAsync(kiosk.Client, first.GetProperty("id").GetGuid());
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().Should().Be("printJobs.cancelWhilePrinting");

        var queue = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue");
        queue.EnumerateArray().Select(entry => entry.GetProperty("status").GetString()).Should().Equal("Printing");
    }

    [DockerFact]
    public async Task One_kiosk_cannot_see_or_touch_another_s_job()
    {
        if (Skip) { return; }

        using var mine = await ReadyKioskAsync("mine");
        using var theirs = await ReadyKioskAsync("theirs");

        var job = await PlacedAsync(mine.Client, 1);
        var id = job.GetProperty("id").GetGuid();

        (await theirs.Client.GetAsync($"/api/print-jobs/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await theirs.Client.GetAsync($"/api/print-jobs/by-code/{job.GetProperty("jobCode").GetString()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CancelAsync(theirs.Client, id)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await theirs.Client.PostAsJsonAsync($"/api/print-jobs/{id}/status", new { status = "Printing" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await theirs.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue")).GetArrayLength().Should().Be(0);
    }

    [DockerFact]
    public async Task Without_payments_a_job_waits_for_one_and_skipping_it_is_refused_outside_development()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("payment");

        // Payments arrive in step 4: a job placed without the flag waits.
        var waiting = await PlacedAsync(kiosk.Client, 1, dev: false);
        waiting.GetProperty("status").GetString().Should().Be("PendingPayment");
        (await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue")).GetArrayLength().Should().Be(0);

        // A host that is not Development refuses the flag.
        using var production = _fixture.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<PrintJobOptions>(options => options.AllowPaymentSkip = false)));
        using var client = production.CreateClient();
        client.DefaultRequestHeaders.Authorization = kiosk.Client.DefaultRequestHeaders.Authorization;

        var refused = await PlaceAsync(client, 1, dev: true);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()
            .Should().Be("printJobs.paymentSkipNotAllowed");
    }

    [DockerFact]
    public async Task A_lost_redis_queue_is_rebuilt_from_mongodb()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("redis");
        var job = await PlacedAsync(kiosk.Client, 1);

        var redis = _fixture.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.KeyDeleteAsync(RedisPrintQueue.KeyFor(kiosk.KioskId));

        var queue = await kiosk.Client.GetFromJsonAsync<JsonElement>("/api/kiosks/me/queue");
        queue.GetArrayLength().Should().Be(1, "MongoDB is the truth");

        (await redis.SortedSetLengthAsync(RedisPrintQueue.KeyFor(kiosk.KioskId))).Should().Be(1, "and the set is rebuilt from it");

        // The reconciliation job does the same for every kiosk.
        await redis.KeyDeleteAsync(RedisPrintQueue.KeyFor(kiosk.KioskId));
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<PrintQueueReconciliationJob>().RunAsync(CancellationToken.None);
        }

        (await redis.SortedSetRankAsync(RedisPrintQueue.KeyFor(kiosk.KioskId), job.GetProperty("id").GetGuid().ToString("N")))
            .Should().Be(0);
    }

    [DockerFact]
    public async Task The_kiosk_hears_its_queue_change_over_its_hub()
    {
        if (Skip) { return; }

        using var kiosk = await ReadyKioskAsync("hub");
        await using var hub = await RawHubConnection.ConnectAsync(_fixture, "kiosk", kiosk.AccessToken);

        var job = await PlacedAsync(kiosk.Client, 1);

        var update = await hub.WaitForAsync("QueueUpdated", TimeSpan.FromSeconds(10));
        update.GetProperty("kioskId").GetGuid().Should().Be(kiosk.KioskId);
        update.GetProperty("queue")[0].GetProperty("jobId").GetGuid().Should().Be(job.GetProperty("id").GetGuid());
    }

    [DockerFact]
    public async Task The_admin_lists_filters_and_cancels_jobs()
    {
        if (Skip) { return; }

        using var admin = await TestKiosks.SuperAdminAsync(_fixture);
        using var kiosk = await ReadyKioskAsync("admin");
        var job = await PlacedAsync(kiosk.Client, 2);
        var id = job.GetProperty("id").GetGuid();

        var page = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/print-jobs?kioskId={kiosk.KioskId}&status=Queued");
        page.GetProperty("total").GetInt64().Should().Be(1);
        page.GetProperty("items")[0].GetProperty("kioskCode").GetString().Should().Be(kiosk.KioskCode);

        var queue = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/kiosks/{kiosk.KioskId}/queue");
        queue.GetArrayLength().Should().Be(1);

        using var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/print-jobs/{id}/cancel")
        {
            Content = JsonContent.Create(new { reason = "test" }),
        };
        cancel.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        (await admin.SendAsync(cancel)).StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/print-jobs/{id}");
        detail.GetProperty("job").GetProperty("status").GetString().Should().Be("Cancelled");
        detail.GetProperty("job").GetProperty("cancelReason").GetString().Should().Be("Admin tərəfindən ləğv edildi");
    }

    [DockerFact]
    public async Task A_kiosk_code_outside_the_pattern_does_not_break_the_next_code()
    {
        if (Skip) { return; }

        // KSK-T… sorts above every KSK-nnnn; the next code used to come back
        // as KSK-0001 again and the activation failed with a duplicate.
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().Kiosks
                .InsertOneAsync(new Kiosk { Code = $"KSK-T{Random.Shared.Next(10000, 99999)}", Name = "Odd code" });
        }

        using var first = await TestKiosks.ActivatedAsync(_fixture, $"code-a-{Guid.NewGuid():N}"[..20]);
        using var second = await TestKiosks.ActivatedAsync(_fixture, $"code-b-{Guid.NewGuid():N}"[..20]);

        first.KioskCode.Should().MatchRegex("^KSK-[0-9]{4}$");
        second.KioskCode.Should().NotBe(first.KioskCode);
    }

    private static async Task<HttpResponseMessage> CancelAsync(HttpClient kiosk, Guid jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/print-jobs/{jobId}/cancel");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        return await kiosk.SendAsync(request);
    }

    /// <summary>Null values are left out of the API's JSON.</summary>
    private static bool IsAbsent(JsonElement element, string property)
        => !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    private static object Profile(string name, decimal bw) => new
    {
        name,
        bwPricePerSide = bw,
        colorPricePerSide = 0.50m,
        minOrder = 0.10m,
        paperMultipliers = new Dictionary<string, decimal> { ["A4"] = 1m, ["A3"] = 2m },
        volumeTiers = Array.Empty<object>(),
    };
}
