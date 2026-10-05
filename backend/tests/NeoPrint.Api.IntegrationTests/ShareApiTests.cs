using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Files;
using NeoPrint.Application.Privacy;
using NeoPrint.Application.Sharing;
using NeoPrint.Domain.Common;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Sharing a file (plan sections 6, 8.2; docs/DECISIONS.md 2026-10-05): sent
/// to an <c>NP-</c> id, seen in "Alınanlar", the recipient's only once saved,
/// and never something other than what was sent.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ShareApiTests
{
    private readonly NeoPrintApiFixture _fixture;

    public ShareApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [PdfiumFact]
    public async Task A_shared_file_waits_in_received_and_saving_makes_the_recipients_own_copy()
    {
        using var sender = await TestUsers.ConfirmedAsync(_fixture);
        using var recipient = await TestUsers.ConfirmedAsync(_fixture);
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, sender.Client, pages: 2);
        var to = await PublicIdAsync(recipient.Id);

        var sent = await SendAsync(sender.Client, fileId, to, message: "Salam, bax bura");
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        var share = await sent.Content.ReadFromJsonAsync<JsonElement>();
        var shareId = share.GetProperty("id").GetGuid();
        share.GetProperty("otherPublicId").GetString().Should().Be(to);

        // The same file to the same person again, before they saved it: the same share, one notification.
        var again = await (await SendAsync(sender.Client, fileId, to)).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("id").GetGuid().Should().Be(shareId);

        var notifications = await recipient.Client.GetFromJsonAsync<JsonElement>("/api/notifications");
        notifications.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "FileShared")
            .Should().ContainSingle()
            .Which.GetProperty("link").GetString().Should().Be("neoprint://shares");

        var received = await ListAsync(recipient.Client, "received");
        var item = received.Single();
        item.GetProperty("availability").GetString().Should().Be("Available");
        item.GetProperty("otherPublicId").GetString().Should().Be(await PublicIdAsync(sender.Id));
        item.GetProperty("message").GetString().Should().Be("Salam, bax bura");
        item.GetProperty("pageCount").GetInt32().Should().Be(2);

        // Nothing is in the recipient's files before they save it.
        (await recipient.Client.GetFromJsonAsync<JsonElement>("/api/files")).GetProperty("items").EnumerateArray()
            .Should().BeEmpty();

        var page = await recipient.Client.GetAsync($"/api/shares/{shareId}/pages/2.png?width=320");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("image/png");

        // Only the recipient: someone else's share is not found.
        (await stranger.Client.GetAsync($"/api/shares/{shareId}/pages/1.png")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.Client.PostAsync($"/api/shares/{shareId}/save", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sender.Client.PostAsync($"/api/shares/{shareId}/save", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var saved = await recipient.Client.PostAsync($"/api/shares/{shareId}/save", null);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var copy = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var copyId = copy.GetProperty("id").GetGuid();
        copyId.Should().NotBe(fileId);
        copy.GetProperty("source").GetString().Should().Be("Shared");
        copy.GetProperty("status").GetString().Should().Be("Ready");
        copy.GetProperty("pageCount").GetInt32().Should().Be(2);

        // A second tap is the same copy, not a second one.
        (await (await recipient.Client.PostAsync($"/api/shares/{shareId}/save", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid().Should().Be(copyId);

        var original = await sender.Client.GetByteArrayAsync($"/api/files/{fileId}/content");
        (await recipient.Client.GetByteArrayAsync($"/api/files/{copyId}/content")).Should().Equal(original);

        // The sender deletes theirs: the saved copy is the recipient's and stays.
        (await sender.Client.DeleteAsync($"/api/files/{fileId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await recipient.Client.GetByteArrayAsync($"/api/files/{copyId}/content")).Should().Equal(original);

        (await ListAsync(recipient.Client, "received")).Single().GetProperty("availability").GetString().Should().Be("Saved");
        (await ListAsync(sender.Client, "sent")).Single().GetProperty("availability").GetString().Should().Be("Saved");
    }

    [PdfiumFact]
    public async Task A_file_deleted_or_changed_after_sending_cannot_be_opened_or_saved()
    {
        using var sender = await TestUsers.ConfirmedAsync(_fixture);
        using var recipient = await TestUsers.ConfirmedAsync(_fixture);
        var to = await PublicIdAsync(recipient.Id);
        var deletedId = await SessionTests.UploadReadyPdfAsync(_fixture, sender.Client, pages: 1);
        var changedId = await SessionTests.UploadReadyPdfAsync(_fixture, sender.Client, pages: 1);

        var deletedShare = await ShareIdAsync(await SendAsync(sender.Client, deletedId, to));
        var changedShare = await ShareIdAsync(await SendAsync(sender.Client, changedId, to));

        (await sender.Client.DeleteAsync($"/api/files/{deletedId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await ChangePdfAsync(changedId);

        var received = (await ListAsync(recipient.Client, "received"))
            .ToDictionary(item => item.GetProperty("id").GetGuid(), item => item.GetProperty("availability").GetString());
        received[deletedShare].Should().Be("Gone");
        received[changedShare].Should().Be("Changed");

        var gone = await recipient.Client.PostAsync($"/api/shares/{deletedShare}/save", null);
        gone.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await gone.Content.ReadAsStringAsync()).Should().Contain("Fayl artıq yoxdur");

        var changed = await recipient.Client.PostAsync($"/api/shares/{changedShare}/save", null);
        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await changed.Content.ReadAsStringAsync()).Should().Contain("dəyişib");

        (await recipient.Client.GetAsync($"/api/shares/{changedShare}/pages/1.png")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await recipient.Client.GetFromJsonAsync<JsonElement>("/api/files")).GetProperty("items").EnumerateArray()
            .Should().BeEmpty("nothing was saved");
    }

    [PdfiumFact]
    public async Task Wrong_ids_yourself_blocked_accounts_and_an_unconfirmed_personal_document_are_refused()
    {
        using var sender = await TestUsers.ConfirmedAsync(_fixture);
        using var recipient = await TestUsers.ConfirmedAsync(_fixture);
        using var blocked = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, sender.Client, pages: 1);

        var malformed = await SendAsync(sender.Client, fileId, "7KQ2MX");
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await malformed.Content.ReadAsStringAsync()).Should().Contain("NP-");

        (await SendAsync(sender.Client, fileId, "NP-000000")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(sender.Client, fileId, await PublicIdAsync(sender.Id))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await BlockAsync(blocked.Id);
        var toBlocked = await SendAsync(sender.Client, fileId, await PublicIdAsync(blocked.Id));
        toBlocked.StatusCode.Should().Be(HttpStatusCode.NotFound, "a blocked account reads as unknown");

        (await SendAsync(recipient.Client, fileId, await PublicIdAsync(sender.Id))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "only the owner shares a file");

        // A personal document goes only after the warning was confirmed.
        await MarkSensitiveAsync(fileId);
        var to = (await PublicIdAsync(recipient.Id)).ToLowerInvariant();
        var unconfirmed = await SendAsync(sender.Client, fileId, to);
        unconfirmed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unconfirmed.Content.ReadAsStringAsync()).Should().Contain("şəxsi sənəddir");

        var confirmed = await SendAsync(sender.Client, fileId, to, confirmSensitive: true);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, "an id is read in any case");

        var saved = await (await recipient.Client.PostAsync(
            $"/api/shares/{await ShareIdAsync(confirmed)}/save", null)).Content.ReadFromJsonAsync<JsonElement>();
        saved.GetProperty("isSensitive").GetBoolean().Should().BeTrue("the copy of a personal document is one too");
        saved.GetProperty("autoDelete").GetString().Should().Be("AfterPrint");

        var tooLong = await SendAsync(sender.Client, fileId, to, message: new string('a', 201), confirmSensitive: true);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tooLong.Content.ReadAsStringAsync()).Should().Contain("200");
    }

    [PdfiumFact]
    public async Task Erasing_an_account_takes_its_shares_both_ways()
    {
        using var sender = await TestUsers.ConfirmedAsync(_fixture);
        using var recipient = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, sender.Client, pages: 1);
        await ShareIdAsync(await SendAsync(sender.Client, fileId, await PublicIdAsync(recipient.Id)));

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPrivacyRecords>().ScrubAsync(sender.Id);
        }

        (await ListAsync(recipient.Client, "received")).Should().BeEmpty();
    }

    [PdfiumFact]
    public async Task The_share_qr_names_the_user()
    {
        using var user = await TestUsers.ConfirmedAsync(_fixture);

        var qr = await user.Client.GetAsync("/api/shares/qr.png");

        qr.StatusCode.Should().Be(HttpStatusCode.OK);
        qr.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        ShareHandlers.QrPrefix.Should().Be("neoprint://share/");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, Guid fileId, string toPublicId, string? message = null, bool confirmSensitive = false)
        => client.PostAsJsonAsync("/api/shares", new { fileId, toPublicId, message, confirmSensitive });

    private static async Task<Guid> ShareIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<IReadOnlyList<JsonElement>> ListAsync(HttpClient client, string which)
        => [.. (await client.GetFromJsonAsync<JsonElement>($"/api/shares/{which}")).GetProperty("items").EnumerateArray()];

    private async Task<string> PublicIdAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(userId))!.PublicId;
    }

    private async Task BlockAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = (await users.FindByIdAsync(userId))!;
        user.Block(DateTime.UtcNow);
        await users.UpdateAsync(user);
    }

    private async Task MarkSensitiveAsync(Guid fileId)
    {
        using var scope = _fixture.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();
        var file = (await files.FindByIdAsync(fileId))!;
        file.IsSensitive = true;
        await files.UpdateAsync(file);
    }

    /// <summary>A new version of the file, as an edit makes: a new PDF.</summary>
    private async Task ChangePdfAsync(Guid fileId)
    {
        using var scope = _fixture.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();
        var file = (await files.FindByIdAsync(fileId))!;
        var actor = ActorContext.System("test");
        var pdf = file.Pdf! with { Key = file.Pdf!.Key + "-v2" };

        file.ApplyEdit(pdf, "pdf", null, keep: 5, actor, DateTime.UtcNow);
        file.BeginConversion(actor, DateTime.UtcNow);
        file.Complete(pdf, 1, actor, DateTime.UtcNow);
        await files.UpdateAsync(file);
    }
}
