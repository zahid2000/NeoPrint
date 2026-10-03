using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Notifications;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Notifications;
using NeoPrint.Infrastructure.Persistence;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Notifications and push (plan sections 6, 8.2 and 8.4; <c>docs/DECISIONS.md</c>
/// T-146): the devices a user registers, the list, and delivery - once per
/// event and per device, the forgotten tokens dropped, the failed ones retried.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class NotificationTests
{
    private readonly NeoPrintApiFixture _fixture;

    public NotificationTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_token_follows_whoever_signed_in_last_and_signing_out_stops_it()
    {
        if (Skip)
        {
            return;
        }

        using var first = await TestUsers.ConfirmedAsync(_fixture);
        using var second = await TestUsers.ConfirmedAsync(_fixture);
        var token = $"shared-phone-{Guid.NewGuid():N}";

        await RegisterDeviceAsync(first.Client, token, "ru", "android");
        await RegisterDeviceAsync(first.Client, token, "ru", "android");
        (await DevicesAsync(first.Id)).Should().ContainSingle(d => d.Token == token && d.Language == "ru" && d.Platform == "android");

        // Someone else signs in on the same phone: the first user's pushes stop going there.
        await RegisterDeviceAsync(second.Client, token, "xx", "toaster");
        (await DevicesAsync(first.Id)).Should().BeEmpty();
        (await DevicesAsync(second.Id)).Should().ContainSingle(d => d.Token == token && d.Language == "az" && d.Platform == "unknown");

        // Not a token at all.
        var empty = await first.Client.PostAsJsonAsync("/api/users/me/fcm-token", new { token = " ", language = "az" });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await empty.Content.ReadAsStringAsync()).Should().Contain("token");

        // The first user cannot sign the second one's phone out.
        (await DeleteDeviceAsync(first.Client, token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DevicesAsync(second.Id)).Should().ContainSingle();

        (await DeleteDeviceAsync(second.Client, token)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DevicesAsync(second.Id)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Each_device_gets_one_push_a_forgotten_one_is_dropped_and_a_failed_one_is_tried_again()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var suffix = Guid.NewGuid().ToString("N");
        var (ok, gone, flaky) = ($"ok-{suffix}", $"gone-{suffix}", $"flaky-{suffix}");
        await RegisterDeviceAsync(user.Client, ok, "az", "android");
        await RegisterDeviceAsync(user.Client, gone, "az", "ios");
        await RegisterDeviceAsync(user.Client, flaky, "en", "web");

        var notify = new NotifyUserCommand(
            user.Id,
            NotificationType.RefundCompleted,
            UserMessage.Of("notifications.refundCompleted.title"),
            UserMessage.Of("notifications.refundCompleted.body", ("amount", 1.50m), ("destination", "Wallet")),
            NotificationLinks.Wallet,
            $"RefundCompleted:test-{suffix}");
        await SendAsync(notify);

        // The failed browser is retried by the bus and reached on the second try...
        await EventuallyAsync(() => Task.FromResult(_fixture.Pushes.To(flaky)), p => p.Count == 1, "the retried push", seconds: 60);
        _fixture.Pushes.Attempts(flaky).Should().Be(2);
        _fixture.Pushes.To(flaky)[0].Body.Should().Be("1.50 ₼ returned to your balance.");

        // ...without the phone, reached the first time, getting it twice.
        _fixture.Pushes.To(ok).Should().ContainSingle().Which.Body.Should().Be("1,50 ₼ qaytarıldı: balansınıza.");

        // FCM no longer knows the iPhone's token: it is dropped from the account.
        _fixture.Pushes.To(gone).Should().BeEmpty();
        (await DevicesAsync(user.Id)).Select(d => d.Token).Should().BeEquivalentTo([ok, flaky]);

        var stored = await EventuallyAsync(
            async () => (await StoredAsync(notify.DedupKey)).Single(),
            n => n.Push == PushState.Sent,
            "the push finished");
        stored.PushedTo.Should().HaveCount(2);

        // The same event told again: nothing new.
        await SendAsync(notify);
        await Task.Delay(1500);
        (await StoredAsync(notify.DedupKey)).Should().ContainSingle();
        _fixture.Pushes.To(ok).Should().ContainSingle();
    }

    [DockerFact]
    public async Task The_list_pages_newest_first_and_only_the_owner_reads_it()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);

        (await ListAsync(user.Client, "az")).GetProperty("items").GetArrayLength().Should().Be(0);

        for (var i = 0; i < 3; i++)
        {
            await SendAsync(new NotifyUserCommand(
                user.Id,
                NotificationType.JobCompleted,
                UserMessage.Of("notifications.jobCompleted.title"),
                UserMessage.Of("notifications.jobCompleted.body", ("code", $"10000{i}"), ("kiosk", "KSK-1")),
                NotificationLinks.Job(Guid.NewGuid()),
                $"JobCompleted:list-{user.Id}-{i}"));
            await Task.Delay(20);
        }

        var first = await user.Client.GetFromJsonAsync<JsonElement>("/api/notifications?take=2");
        var items = first.GetProperty("items").EnumerateArray().ToList();
        items.Select(n => n.GetProperty("body").GetString()).Should().Equal(
            "100002 sifarişi KSK-1 kioskunda çap olundu. Vərəqləri götürməyi unutmayın.",
            "100001 sifarişi KSK-1 kioskunda çap olundu. Vərəqləri götürməyi unutmayın.");
        first.GetProperty("unread").GetInt64().Should().Be(3);

        var next = first.GetProperty("next").GetDateTime().ToUniversalTime();
        var second = await user.Client.GetFromJsonAsync<JsonElement>($"/api/notifications?take=2&before={next:O}");
        second.GetProperty("items").EnumerateArray().Single().GetProperty("body").GetString().Should().StartWith("100000");
        second.TryGetProperty("next", out _).Should().BeFalse("the last page has no next");

        // Someone else's: not found, and still unread.
        var id = items[0].GetProperty("id").GetGuid();
        (await stranger.Client.PostAsync($"/api/notifications/{id}/read", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ListAsync(stranger.Client, "az")).GetProperty("items").GetArrayLength().Should().Be(0);

        var read = await (await user.Client.PostAsync($"/api/notifications/{id}/read", null)).Content.ReadFromJsonAsync<JsonElement>();
        var readAt = read.GetProperty("readAt").GetDateTime();
        var again = await (await user.Client.PostAsync($"/api/notifications/{id}/read", null)).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("readAt").GetDateTime().Should().BeCloseTo(readAt, TimeSpan.FromMilliseconds(1), "opening it again keeps the first time");
        (await ListAsync(user.Client, "az")).GetProperty("unread").GetInt64().Should().Be(2);
    }

    internal static async Task RegisterDeviceAsync(HttpClient user, string token, string language, string platform)
    {
        var response = await user.PostAsJsonAsync("/api/users/me/fcm-token", new { token, language, platform });
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    internal static async Task<JsonElement> ListAsync(HttpClient user, string language)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/notifications");
        request.Headers.AcceptLanguage.ParseAdd(language);
        var response = await user.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> DeleteDeviceAsync(HttpClient user, string token)
        => user.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/users/me/fcm-token") { Content = JsonContent.Create(new { token }) });

    private async Task<IReadOnlyList<Domain.Users.PushDevice>> DevicesAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<MongoContext>().Users.Find(u => u.Id == userId).SingleAsync();
        return user.FcmTokens;
    }

    private async Task<List<Notification>> StoredAsync(string dedupKey)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Notifications.Find(n => n.DedupKey == dedupKey).ToListAsync();
    }

    private async Task SendAsync(IRequest request)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }
}
