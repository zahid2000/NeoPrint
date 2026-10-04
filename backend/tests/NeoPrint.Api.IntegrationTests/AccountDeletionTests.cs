using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Notifications;
using NeoPrint.Application.Privacy;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Notifications;
using NeoPrint.Domain.Support;
using NeoPrint.Domain.Users;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// "Hesabımı sil" (plan section 2.13, <c>AccountDeletionJob</c>;
/// <c>docs/DECISIONS.md</c> T-147): refused while money or orders are still
/// open; closed at once with the password and "SİL"; after the grace hours the
/// files go from every provider and the personal details from the records that stay.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountDeletionTests
{
    private static readonly object BlackAndWhite = new
    {
        copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit",
    };

    private readonly NeoPrintApiFixture _fixture;

    public AccountDeletionTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [PdfiumFact]
    public async Task A_scheduled_print_keeps_the_account_until_it_is_cancelled_and_the_confirmation_and_password_are_checked()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "delete-blocked");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 3);
        var created = await PostAsync(user.Client, "/api/schedules", new
        {
            kiosk = kiosk.KioskCode, fileId, options = BlackAndWhite, scheduledAt = DateTime.UtcNow.AddHours(2), expectedAmount = 0.30m,
        });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var scheduleId = body.GetProperty("scheduleId").GetGuid();
        var paymentId = body.GetProperty("paymentId").GetGuid();
        (await PostAsync(user.Client, $"/api/payments/{paymentId}/pay-with-app", new { source = "Card", useBonus = false, expectedAmount = 0.30m }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var check = await EventuallyAsync(
            () => user.Client.GetFromJsonAsync<JsonElement>("/api/users/me/delete"),
            c => c.GetProperty("blockers").GetProperty("scheduledPrints").GetInt32() == 1,
            "the schedule counted");
        check.GetProperty("blockers").GetProperty("any").GetBoolean().Should().BeTrue();

        var refused = await DeleteAsync(user.Client, TestUsers.Password, "SİL");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("planlanmış çap");

        // Cancelled (its refund to the balance must finish too): now only the words are checked.
        (await user.Client.DeleteAsync($"/api/schedules/{scheduleId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await EventuallyAsync(
            () => user.Client.GetFromJsonAsync<JsonElement>("/api/users/me/delete"),
            c => !c.GetProperty("blockers").GetProperty("any").GetBoolean(),
            "nothing in the way");

        (await DeleteAsync(user.Client, TestUsers.Password, "sil olsun")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DeleteAsync(user.Client, "Wrong!Password1", "SİL")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await user.Client.GetAsync("/api/users/me/qr")).StatusCode.Should().Be(HttpStatusCode.OK, "still an account");
    }

    [PdfiumFact]
    public async Task Deleted_at_once_erased_after_the_grace_hours_and_the_email_is_free_again()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages: 1);
        await SendAsync(new NotifyUserCommand(
            user.Id, NotificationType.JobCompleted, UserMessage.Of("notifications.jobCompleted.title"),
            UserMessage.Of("notifications.jobCompleted.body", ("code", "100000"), ("kiosk", "KSK-1")), null, $"JobCompleted:del-{user.Id}"));
        var claim = SupportClaim.Open(ClaimChannel.App, null, null, null, user.Id, "Mənim adım Əli, telefon 050 000 00 00", "ali@example.com", DateTime.UtcNow);
        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().SupportClaims.InsertOneAsync(claim);
        }

        var deleted = await DeleteAsync(user.Client, TestUsers.Password, "sil");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());

        // Closed at once: the session, the access token in hand, signing in again.
        (await user.Client.GetAsync("/api/users/me/qr")).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        using var anonymous = _fixture.CreateApiClient();
        (await anonymous.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = TestUsers.Password }))
            .StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = user.RefreshToken }))
            .StatusCode.Should().NotBe(HttpStatusCode.OK);

        var account = await UserAsync(user.Id);
        account.Status.Should().Be(UserStatus.Deleted);
        account.Email.Should().StartWith("deleted-").And.NotContain("@");
        account.FullName.Should().BeNull();
        account.PasswordHash.Should().BeEmpty();

        // The address is free for a new account at once.
        using var again = await TestUsers.ConfirmedAsync(_fixture, user.Email);
        again.Id.Should().NotBe(user.Id);

        // Within the grace hours the files are still there; past them they go.
        await SendAsync(new EraseDeletedAccountsCommand());
        (await FileAsync(fileId)).DeletedAt.Should().BeNull();

        using (var scope = _fixture.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MongoContext>().Users.UpdateOneAsync(
                Builders<User>.Filter.Eq(u => u.Id, user.Id),
                Builders<User>.Update.Set(u => u.DeletedAt, DateTime.UtcNow.AddHours(-25)));
        }

        (await SendAsync(new EraseDeletedAccountsCommand())).Should().BeGreaterThanOrEqualTo(1);
        (await FileAsync(fileId)).DeletedAt.Should().NotBeNull();
        (await UserAsync(user.Id)).ErasedAt.Should().NotBeNull();

        using (var scope = _fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MongoContext>();
            (await context.Notifications.CountDocumentsAsync(n => n.UserId == user.Id)).Should().Be(0);
            var consents = await context.Consents.Find(c => c.UserId == user.Id).ToListAsync();
            consents.Should().NotBeEmpty("which version was accepted stays as proof");
            consents.Should().OnlyContain(c => c.Ip == null && c.DeviceId == null);
            var scrubbed = await context.SupportClaims.Find(c => c.Id == claim.Id).SingleAsync();
            scrubbed.Text.Should().Be("—");
            scrubbed.Contact.Should().BeNull();
        }

        // Once is enough.
        await SendAsync(new EraseDeletedAccountsCommand());
        (await UserAsync(again.Id)).Status.Should().Be(UserStatus.Active, "the new account with the same address is another account");
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient user, string password, string confirmation)
        => PostAsync(user, "/api/users/me/delete", new { password, confirmation });

    private async Task<User> UserAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Users.Find(u => u.Id == id).SingleAsync();
    }

    private async Task<Domain.Files.StoredFile> FileAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Files.Find(f => f.Id == id).SingleAsync();
    }

    private async Task SendAsync(IRequest request)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
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
