using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Application.Customers;
using NeoPrint.Application.Files;
using NeoPrint.Application.Referrals;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Referrals;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The referral programme and a customer's files as the admin panel shows
/// them (plan section 8A.3): numbers, the top inviters, the history - and
/// file metadata with the name masked, never the content.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminReferralApiTests
{
    private readonly NeoPrintApiFixture _fixture;

    public AdminReferralApiTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task The_programme_shows_its_numbers_top_inviters_and_history_by_public_id()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await AdminAsync("dev-admin@neoprint.local");
        using var inviter = await TestUsers.ConfirmedAsync(_fixture);
        var code = (await inviter.Client.GetFromJsonAsync<JsonElement>("/api/referrals/me")).GetProperty("code").GetString()!;
        var before = (await admin.GetFromJsonAsync<JsonElement>("/api/admin/referrals")).GetProperty("totals");

        using var rewarded = await TestUsers.ConfirmedAsync(_fixture, referralCode: code);
        using var held = await TestUsers.ConfirmedAsync(_fixture, referralCode: code);
        using var waiting = await TestUsers.ConfirmedAsync(_fixture, referralCode: code);
        await DecideAsync(rewarded.Id, referral => referral.Reward(Guid.NewGuid(), 10, 2, ActorContext.System("test"), DateTime.UtcNow));
        await DecideAsync(held.Id, referral => referral.Withhold(Guid.NewGuid(), ReferralHold.SameDevice, DateTime.UtcNow));

        var page = await admin.GetFromJsonAsync<JsonElement>("/api/admin/referrals?pageSize=100");
        var totals = page.GetProperty("totals");
        long Delta(string name) => totals.GetProperty(name).GetInt64() - before.GetProperty(name).GetInt64();
        (Delta("registered"), Delta("rewarded"), Delta("held"), Delta("waiting"), Delta("bonusPagesGiven"))
            .Should().Be((3, 1, 1, 1, 12), "inviter and invitee pages together");

        var inviterId = await PublicIdAsync(inviter.Id);
        var mine = page.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("inviter").GetProperty("publicId").GetString() == inviterId)
            .ToDictionary(item => item.GetProperty("invitee").GetProperty("id").GetGuid());
        mine.Should().HaveCount(3);
        (mine[rewarded.Id].GetProperty("status").GetString(), mine[rewarded.Id].GetProperty("inviterBonusPages").GetInt32())
            .Should().Be(("Rewarded", 10));
        (mine[held.Id].GetProperty("status").GetString(), mine[held.Id].GetProperty("hold").GetString())
            .Should().Be(("Registered", "SameDevice"));
        mine[waiting.Id].TryGetProperty("qualifiedAt", out _).Should().BeFalse("not decided yet");
        mine[rewarded.Id].TryGetProperty("qualifiedAt", out _).Should().BeTrue();
        page.GetRawText().Should().NotContain(inviter.Email, "the referral pages name people by public id, not email");

        // Each filter is its own slice of the history.
        foreach (var (filter, user) in new[] { ("Rewarded", rewarded), ("Held", held), ("Waiting", waiting) })
        {
            var slice = (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/referrals?status={filter}&pageSize=100"))
                .GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("inviter").GetProperty("publicId").GetString() == inviterId)
                .ToList();
            slice.Should().ContainSingle().Which.GetProperty("invitee").GetProperty("id").GetGuid().Should().Be(user.Id);
        }

        (await admin.GetAsync("/api/admin/referrals?status=Nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // The customer's own page: whom they invited, and who invited them.
        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users/{inviter.Id}");
        var referrals = detail.GetProperty("referrals");
        referrals.TryGetProperty("invitedBy", out _).Should().BeFalse("nobody invited the inviter");
        (referrals.GetProperty("totals").GetProperty("registered").GetInt64(), referrals.GetProperty("totals").GetProperty("bonusPagesEarned").GetInt64())
            .Should().Be((3, 10));
        referrals.GetProperty("items").GetArrayLength().Should().Be(3);

        (await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users/{waiting.Id}"))
            .GetProperty("referrals").GetProperty("invitedBy").GetProperty("publicId").GetString().Should().Be(inviterId);
    }

    [DockerFact]
    public async Task Only_a_role_with_referrals_view_sees_the_programme()
    {
        if (Skip)
        {
            return;
        }

        using var @operator = await AdminAsync("dev-operator@neoprint.local");
        using var user = await TestUsers.ConfirmedAsync(_fixture);

        (await @operator.GetAsync("/api/admin/referrals")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.Client.GetAsync("/api/admin/referrals")).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task A_customers_files_are_metadata_with_the_name_masked()
    {
        if (Skip)
        {
            return;
        }

        using var admin = await AdminAsync("dev-admin@neoprint.local");
        using var user = await TestUsers.ConfirmedAsync(_fixture);
        var ordinary = await UploadAsync(user.Client, "Hesabat 2026.pdf");
        var personal = await UploadAsync(user.Client, "Əli Məmmədov vəsiqə.pdf");
        await MarkSensitiveAsync(personal);

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/users/{user.Id}");
        var files = detail.GetProperty("files").EnumerateArray().ToDictionary(file => file.GetProperty("id").GetGuid());

        files[ordinary].GetProperty("maskedName").GetString().Should().Be("He•••.pdf");
        (files[ordinary].GetProperty("extension").GetString(), files[ordinary].GetProperty("source").GetString(), files[ordinary].GetProperty("isSensitive").GetBoolean())
            .Should().Be(("pdf", "Uploaded", false));
        files[ordinary].GetProperty("sizeBytes").GetInt64().Should().BePositive();

        files[personal].GetProperty("maskedName").GetString().Should().Be("•••.pdf", "a personal document's name is hidden whole");
        files[personal].GetProperty("isSensitive").GetBoolean().Should().BeTrue();

        var raw = detail.GetRawText();
        raw.Should().NotContain("Hesabat").And.NotContain("Məmmədov", "the full name never reaches the panel");

        FileNameMask.Mask("ab.pdf", sensitive: false).Should().Be("•••.pdf", "two letters would be the whole name");
        FileNameMask.Mask("📄📄📄 plan", sensitive: false).Should().Be("📄📄•••", "whole letters, not halves of an emoji");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<HttpClient> AdminAsync(string email)
    {
        var client = _fixture.CreateApiClient();
        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new { email, password = NeoPrintApiFixture.DevTestPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }

    private static async Task<Guid> UploadAsync(HttpClient user, string name)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(ConversionTests.OnePagePdf());
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, "file", name);

        var upload = await user.PostAsync("/api/files", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created, await upload.Content.ReadAsStringAsync());

        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task MarkSensitiveAsync(Guid fileId)
    {
        using var scope = _fixture.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();

        // The conversion consumer may be saving the same file: its row version wins, this is tried again.
        for (var attempt = 0; ; attempt++)
        {
            var file = (await files.FindByIdAsync(fileId))!;
            file.IsSensitive = true;

            try
            {
                await files.UpdateAsync(file);
                return;
            }
            catch (ConcurrencyException) when (attempt < 5)
            {
                await Task.Delay(200);
            }
        }
    }

    private async Task DecideAsync(Guid inviteeId, Action<Referral> decide)
    {
        using var scope = _fixture.Services.CreateScope();
        var referrals = scope.ServiceProvider.GetRequiredService<IReferralRepository>();
        var referral = (await referrals.FindByInviteeAsync(inviteeId))!;
        decide(referral);
        await referrals.UpdateAsync(referral);
    }

    private async Task<string> PublicIdAsync(Guid userId)
    {
        using var scope = _fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<Application.Abstractions.IUserRepository>().FindByIdAsync(userId))!.PublicId;
    }
}
