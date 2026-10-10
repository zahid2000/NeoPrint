using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Referrals;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Referrals;
using NeoPrint.Domain.Settings;

using static NeoPrint.Api.IntegrationTests.PaymentTestKit;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Referral rewards (plan section 2.6; docs/DECISIONS.md 2026-10-05): decided
/// once, by the invitee's first print paid with money, and never twice.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ReferralApiTests
{
    private static readonly object BlackAndWhite = new
    {
        copies = 1, pageRange = (string?)null, color = false, duplex = false, paper = "A4", orientation = "Auto", pagesPerSheet = 1, scaling = "Fit",
    };

    private readonly NeoPrintApiFixture _fixture;

    public ReferralApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [PdfiumFact]
    public async Task The_first_paid_print_rewards_both_once_and_the_inviter_hears_it()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "referral-reward");
        using var inviter = await TestUsers.ConfirmedAsync(_fixture);
        var code = (await inviter.Client.GetFromJsonAsync<JsonElement>("/api/referrals/me")).GetProperty("code").GetString()!;
        using var invitee = await TestUsers.ConfirmedAsync(_fixture, referralCode: code.ToLowerInvariant(), deviceId: Guid.NewGuid().ToString("N"));

        (await ReferralAsync(invitee.Id))!.Status.Should().Be(ReferralStatus.Registered);

        await WithSettingsAsync(
            new() { [SettingKeys.ReferralInviteeBonusPages] = "3" },
            async () =>
            {
                var jobId = await PrintPaidAsync(invitee, kiosk, pages: 3, useBonus: false);

                var referral = (await EventuallyAsync(() => ReferralAsync(invitee.Id), r => r?.IsDecided == true, "the referral decided"))!;
                (referral.Status, referral.QualifyingJobId, referral.InviterBonusPages, referral.InviteeBonusPages)
                    .Should().Be((ReferralStatus.Rewarded, jobId, 10, 3));

                (await BonusPagesAsync(inviter)).Should().Be(10);
                (await BonusPagesAsync(invitee)).Should().Be(3);

                // The same event again: decided, nothing more is credited.
                await SendAsync(new RewardReferralCommand(jobId));
                (await BonusPagesAsync(inviter)).Should().Be(10);

                var notification = await EventuallyAsync(
                    () => NotificationTests.ListAsync(inviter.Client, "az"),
                    page => page.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("type").GetString() == "ReferralRewarded"),
                    "the inviter told");
                notification.GetProperty("items").EnumerateArray()
                    .Single(item => item.GetProperty("type").GetString() == "ReferralRewarded")
                    .GetProperty("body").GetString().Should().Contain("10 bonus səhifə");
            });

        var me = await inviter.Client.GetFromJsonAsync<JsonElement>("/api/referrals/me");
        me.GetProperty("link").GetString().Should().Be("https://neoprint.az/r/" + code);
        (me.GetProperty("invited").GetInt64(), me.GetProperty("rewarded").GetInt64(), me.GetProperty("bonusPagesEarned").GetInt64())
            .Should().Be((1, 1, 10));

        var qr = await inviter.Client.GetAsync("/api/referrals/me/qr.png");
        qr.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
    }

    [PdfiumFact]
    public async Task Only_bonus_pages_is_not_a_paid_print_and_one_job_takes_at_most_five_of_them()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "referral-bonus");
        using var inviter = await TestUsers.ConfirmedAsync(_fixture);
        var code = (await inviter.Client.GetFromJsonAsync<JsonElement>("/api/referrals/me")).GetProperty("code").GetString()!;
        using var invitee = await TestUsers.ConfirmedAsync(_fixture, referralCode: code, deviceId: Guid.NewGuid().ToString("N"));
        await GiveBonusPagesAsync(invitee.Id, 20);

        // Three pages, all of them bonus: printed, but not paid with money.
        var bonusOnly = await PrintPaidAsync(invitee, kiosk, pages: 3, useBonus: true);
        await SendAsync(new RewardReferralCommand(bonusOnly));
        (await ReferralAsync(invitee.Id))!.IsDecided.Should().BeFalse("a print paid only with bonus pages does not count");

        // Seven pages: five bonus pages at most (Referral.MaxBonusPagesPerJob), the rest is money - it counts.
        var mixed = await PrintPaidAsync(invitee, kiosk, pages: 7, useBonus: true);
        (await BonusPagesAsync(invitee)).Should().Be(20 - 3 - 5, "one job takes at most five bonus pages");

        var referral = (await EventuallyAsync(() => ReferralAsync(invitee.Id), r => r?.IsDecided == true, "the referral decided"))!;
        (referral.Status, referral.QualifyingJobId).Should().Be((ReferralStatus.Rewarded, mixed));
    }

    [PdfiumFact]
    public async Task The_same_device_and_the_monthly_limit_hold_the_reward_back()
    {
        if (Skip)
        {
            return;
        }

        using var kiosk = await ReadyKioskAsync(_fixture, "referral-hold");
        using var inviter = await TestUsers.ConfirmedAsync(_fixture, deviceId: "phone-of-the-inviter-" + Guid.NewGuid().ToString("N"));
        var code = (await inviter.Client.GetFromJsonAsync<JsonElement>("/api/referrals/me")).GetProperty("code").GetString()!;
        var inviterDevice = (await UserAsync(inviter.Id)).DeviceIds.First();

        // Registered from the inviter's own phone: the registration goes through, the bonus does not.
        using var sameDevice = await TestUsers.ConfirmedAsync(_fixture, referralCode: code, deviceId: inviterDevice);
        await PrintPaidAsync(sameDevice, kiosk, pages: 3, useBonus: false);
        var held = (await EventuallyAsync(() => ReferralAsync(sameDevice.Id), r => r?.IsDecided == true, "the referral decided"))!;
        (held.Status, held.Hold).Should().Be((ReferralStatus.Registered, ReferralHold.SameDevice));
        (await BonusPagesAsync(inviter)).Should().Be(0);

        await WithSettingsAsync(
            new() { [SettingKeys.ReferralMaxRewardsPerMonth] = "1" },
            async () =>
            {
                using var first = await TestUsers.ConfirmedAsync(_fixture, referralCode: code, deviceId: Guid.NewGuid().ToString("N"));
                using var second = await TestUsers.ConfirmedAsync(_fixture, referralCode: code, deviceId: Guid.NewGuid().ToString("N"));

                await PrintPaidAsync(first, kiosk, pages: 3, useBonus: false);
                (await EventuallyAsync(() => ReferralAsync(first.Id), r => r?.IsDecided == true, "the first decided"))!.Status
                    .Should().Be(ReferralStatus.Rewarded);

                await PrintPaidAsync(second, kiosk, pages: 3, useBonus: false);
                var limited = (await EventuallyAsync(() => ReferralAsync(second.Id), r => r?.IsDecided == true, "the second decided"))!;
                (limited.Status, limited.Hold).Should().Be((ReferralStatus.Registered, ReferralHold.MonthlyLimit));
                (await BonusPagesAsync(inviter)).Should().Be(10, "one reward this month");
            });

        var me = await inviter.Client.GetFromJsonAsync<JsonElement>("/api/referrals/me");
        (me.GetProperty("invited").GetInt64(), me.GetProperty("rewarded").GetInt64(), me.GetProperty("waiting").GetInt64())
            .Should().Be((3, 1, 0));
    }

    [DockerFact]
    public async Task An_unknown_code_or_none_registers_without_an_invitation()
    {
        if (Skip)
        {
            return;
        }

        using var unknown = await TestUsers.ConfirmedAsync(_fixture, referralCode: "NP000000");
        using var none = await TestUsers.ConfirmedAsync(_fixture);

        (await ReferralAsync(unknown.Id)).Should().BeNull();
        (await ReferralAsync(none.Id)).Should().BeNull();
        FluentActions.Invoking(() => Referral.Register(unknown.Id, unknown.Id, DateTime.UtcNow))
            .Should().Throw<DomainException>("nobody invites themselves");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>A scheduled print, paid in the app with a card (and bonus pages when asked), sent and printed.</summary>
    private async Task<Guid> PrintPaidAsync(TestUser user, TestKiosk kiosk, int pages, bool useBonus)
    {
        var fileId = await SessionTests.UploadReadyPdfAsync(_fixture, user.Client, pages);
        var created = await PostAsync(user.Client, "/api/schedules", new
        {
            kiosk = kiosk.KioskCode, fileId, options = BlackAndWhite, scheduledAt = DateTime.UtcNow.AddHours(2), expectedAmount = pages * 0.10m,
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        var paid = await PostAsync(
            user.Client, $"/api/payments/{body.GetProperty("paymentId").GetGuid()}/pay-with-app", new { source = "Card", useBonus });
        paid.StatusCode.Should().Be(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());

        var scheduleId = body.GetProperty("scheduleId").GetGuid();
        var jobId = body.GetProperty("jobId").GetGuid();
        await EventuallyAsync(
            async () => (await user.Client.GetFromJsonAsync<JsonElement>("/api/schedules")).GetProperty("items").EnumerateArray()
                .Single(s => s.GetProperty("id").GetGuid() == scheduleId),
            schedule => schedule.GetProperty("status").GetString() == "Scheduled",
            "the schedule paid");

        var sent = await PostAsync(user.Client, $"/api/schedules/{scheduleId}/print-now", new { });
        // Straight after paying, even with bonus pages that settle at once (T-191).
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        await ReportAsync(kiosk.Client, jobId, "Printing");
        await ReportAsync(kiosk.Client, jobId, "Completed", sides: pages, sheets: pages);

        return jobId;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    private static async Task<int> BonusPagesAsync(TestUser user)
        => (await user.Client.GetFromJsonAsync<JsonElement>("/api/wallet")).GetProperty("bonusPages").GetInt32();

    private async Task<Referral?> ReferralAsync(Guid inviteeId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IReferralRepository>().FindByInviteeAsync(inviteeId);
    }

    private async Task<Domain.Users.User> UserAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IUserRepository>().FindByIdAsync(id))!;
    }

    private async Task GiveBonusPagesAsync(Guid userId, int pages)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Application.Wallets.WalletLedger>().PostAsync(
            userId,
            Domain.Wallets.WalletTransactionType.AdminAdjust,
            Money.Zero,
            pages,
            "test",
            $"test:bonus:{Guid.NewGuid():N}",
            null,
            DateTime.UtcNow,
            "test",
            CancellationToken.None);
    }

    /// <summary>Runs with some settings changed and puts them back: the database is shared by the collection.</summary>
    private async Task WithSettingsAsync(Dictionary<string, string> values, Func<Task> body)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();
        var before = (await store.ListAsync()).Where(s => values.ContainsKey(s.Key)).ToDictionary(s => s.Key, s => s.Value);

        await store.UpdateAsync(values, null);

        try
        {
            await body();
        }
        finally
        {
            await store.UpdateAsync(before, null);
        }
    }

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }

    private async Task SendAsync(IRequest request)
    {
        using var scope = _fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }
}
