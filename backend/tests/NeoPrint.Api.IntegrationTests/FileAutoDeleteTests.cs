using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Amazon.S3.Model;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Files;
using NeoPrint.Domain.Files;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Files that delete themselves (plan section 2.13, <c>FileAutoDeleteJob</c>;
/// <c>docs/DECISIONS.md</c> T-147): after a print - the last one, when another
/// order or a scheduled print still needs it - or a number of days after the
/// upload; from every storage object, with a notification.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FileAutoDeleteTests
{
    private static readonly object BlackAndWhite = new
    {
        copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit",
    };

    private readonly NeoPrintApiFixture _fixture;

    public FileAutoDeleteTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [PdfiumFact]
    public async Task After_print_it_goes_when_the_last_order_for_it_printed_and_the_owner_is_told()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "auto-delete-print");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);
        (await SetRuleAsync(user.Client, fileId, "AfterPrint")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Two scheduled prints of it: the first printed, the second still waiting.
        var first = await ScheduleAndPayAsync(user.Client, kiosk.KioskCode, fileId);
        var second = await ScheduleAndPayAsync(user.Client, kiosk.KioskCode, fileId);
        await PrintAsync(user.Client, kiosk.Client, first);

        await EventuallyAsync(() => FileAsync(fileId), f => f.PrintedAt is not null, "the print noted");
        await SendAsync(new RunFileAutoDeleteCommand());
        (await FileAsync(fileId)).DeletedAt.Should().BeNull("the second scheduled print still prints it");
        (await StoredKeysAsync(user.Id, fileId)).Should().NotBeEmpty();

        // The second one printed too: now it goes, every object of it.
        await PrintAsync(user.Client, kiosk.Client, second);
        var gone = await EventuallyAsync(() => FileAsync(fileId), f => f.DeletedAt is not null, "the file deleted");
        gone.AutoDelete.Should().Be(AutoDeleteRule.AfterPrint);
        (await StoredKeysAsync(user.Id, fileId)).Should().BeEmpty();
        (await user.Client.GetAsync($"/api/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var notice = await EventuallyAsync(
            () => NotificationTests.ListAsync(user.Client, "az"),
            p => p.GetProperty("items").EnumerateArray().Any(n => n.GetProperty("type").GetString() == "FileAutoDeleted"),
            "the notification");
        var item = notice.GetProperty("items").EnumerateArray().Single(n => n.GetProperty("type").GetString() == "FileAutoDeleted");
        item.GetProperty("body").GetString().Should().Be("\"hesabat.pdf\" seçdiyiniz qaydaya görə silindi: çapdan sonra.");
        item.GetProperty("link").GetString().Should().Be("neoprint://files");
    }

    [PdfiumFact]
    public async Task A_number_of_days_counts_from_the_upload_and_never_means_never()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var day = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 1);
        var kept = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 1);
        (await SetRuleAsync(user.Client, day, "AfterOneDay")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetRuleAsync(user.Client, kept, "Never")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetRuleAsync(user.Client, kept, "Sometimes")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Not a day yet: nothing.
        await SendAsync(new RunFileAutoDeleteCommand());
        (await FileAsync(day)).DeletedAt.Should().BeNull();

        // Both uploaded two days ago.
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().Files.UpdateManyAsync(
                Builders<StoredFile>.Filter.In(f => f.Id, [day, kept]),
                Builders<StoredFile>.Update.Set(f => f.CreatedAt, DateTime.UtcNow.AddDays(-2)));
        }

        (await SendAsync(new RunFileAutoDeleteCommand())).Should().BeGreaterThanOrEqualTo(1);
        (await FileAsync(day)).DeletedAt.Should().NotBeNull();
        (await StoredKeysAsync(user.Id, day)).Should().BeEmpty();
        (await FileAsync(kept)).DeletedAt.Should().BeNull();

        // Again: nothing more, and the notification is one.
        await SendAsync(new RunFileAutoDeleteCommand());
        var items = (await EventuallyAsync(
                () => NotificationTests.ListAsync(user.Client, "en"),
                p => p.GetProperty("items").GetArrayLength() > 0,
                "the notification"))
            .GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle().Which.GetProperty("body").GetString().Should().EndWith("after 1 day.");
    }

    private static async Task<HttpResponseMessage> SetRuleAsync(HttpClient user, Guid fileId, string rule)
        => await user.PutAsJsonAsync($"/api/files/{fileId}/auto-delete", new { rule });

    private static async Task<(Guid ScheduleId, Guid JobId)> ScheduleAndPayAsync(HttpClient user, string kiosk, Guid fileId)
    {
        var created = await PostAsync(user, "/api/schedules", new
        {
            kiosk, fileId, options = BlackAndWhite, scheduledAt = DateTime.UtcNow.AddHours(2), expectedAmount = 0.30m,
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var paid = await PostAsync(
            user, $"/api/payments/{body.GetProperty("paymentId").GetGuid()}/pay-with-app", new { source = "Card", useBonus = false, expectedAmount = 0.30m });
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        var scheduleId = body.GetProperty("scheduleId").GetGuid();
        await EventuallyAsync(
            async () => (await user.GetFromJsonAsync<JsonElement>("/api/schedules")).GetProperty("items").EnumerateArray()
                .Single(s => s.GetProperty("id").GetGuid() == scheduleId).GetProperty("status").GetString(),
            status => status == "Scheduled",
            "the schedule paid");

        return (scheduleId, body.GetProperty("jobId").GetGuid());
    }

    private static async Task PrintAsync(HttpClient user, HttpClient kiosk, (Guid ScheduleId, Guid JobId) order)
    {
        var sent = await PostAsync(user, $"/api/schedules/{order.ScheduleId}/print-now", new { });
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        await ReportAsync(kiosk, order.JobId, "Printing");
        await ReportAsync(kiosk, order.JobId, "Completed", sides: 3, sheets: 3);
    }

    private async Task<StoredFile> FileAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Files.Find(f => f.Id == id).SingleAsync();
    }

    private async Task<IReadOnlyList<string>> StoredKeysAsync(Guid userId, Guid fileId)
    {
        using var direct = _fixture.CreateDirectStorageClient();
        var listing = await direct.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = NeoPrintApiFixture.FilesBucket,
            Prefix = $"files/{userId:N}/{fileId:N}/",
        });
        return [.. (listing.S3Objects ?? []).Select(o => o.Key)];
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }
}
