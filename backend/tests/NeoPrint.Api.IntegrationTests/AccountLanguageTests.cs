using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Domain.Users;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The account's language (<c>PUT users/me</c>, docs/DECISIONS.md T-152): kept
/// on the account, returned at the next sign-in on any device, and followed
/// by every push device of the account.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccountLanguageTests
{
    private readonly NeoPrintApiFixture _fixture;

    public AccountLanguageTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task The_language_chosen_in_the_app_is_the_account_s_on_every_device()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        (await user.Client.PostAsJsonAsync("/api/users/me/fcm-token", new { token = $"phone-a-{Guid.NewGuid():N}", language = "az", platform = "android" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.Client.PostAsJsonAsync("/api/users/me/fcm-token", new { token = $"phone-b-{Guid.NewGuid():N}", language = "ru", platform = "ios" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await user.Client.PutAsJsonAsync("/api/users/me", new { language = "en" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.Client.PutAsJsonAsync("/api/users/me", new { language = "en" })).StatusCode.Should().Be(HttpStatusCode.NoContent, "a repeat changes nothing");

        var stored = await FindAsync(user.Id);
        stored.Language.Should().Be("en");
        stored.FcmTokens.Should().HaveCount(2).And.OnlyContain(device => device.Language == "en");

        // Another phone signs in: the account says English.
        using var other = _fixture.CreateApiClient();
        var login = await other.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = TestUsers.Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user").GetProperty("language").GetString().Should().Be("en");
    }

    // T-268 (the owner, 2026-10-14): "I sign out and the push still comes".
    [DockerFact]
    public async Task Signing_out_takes_the_device_off_the_account_s_push_and_no_other_device()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var stranger = await TestUsers.ConfirmedAsync(_fixture);
        var phone = $"phone-{Guid.NewGuid():N}";
        var tablet = $"tablet-{Guid.NewGuid():N}";
        var theirs = $"theirs-{Guid.NewGuid():N}";

        foreach (var token in new[] { phone, tablet })
        {
            (await user.Client.PostAsJsonAsync("/api/users/me/fcm-token", new { token, language = "az", platform = "android" }))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        (await stranger.Client.PostAsJsonAsync("/api/users/me/fcm-token", new { token = theirs, language = "az", platform = "android" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The sign-out carries no access token: it may have run out long before.
        using var anonymous = _fixture.CreateApiClient();

        // Somebody else's device is not theirs to take off.
        (await anonymous.PostAsJsonAsync("/api/auth/logout", new { refreshToken = "not-a-token", fcmToken = phone }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FindAsync(user.Id)).FcmTokens.Should().HaveCount(2, "a refresh token that is nobody's signs nobody out");

        (await anonymous.PostAsJsonAsync("/api/auth/logout", new { refreshToken = user.RefreshToken, fcmToken = phone }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await FindAsync(user.Id)).FcmTokens.Select(device => device.Token).Should().Equal([tablet], "the phone signed out; the tablet did not");

        // The same sign-out again, and one that names another account's device: nothing more happens.
        (await anonymous.PostAsJsonAsync("/api/auth/logout", new { refreshToken = user.RefreshToken, fcmToken = theirs }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await FindAsync(stranger.Id)).FcmTokens.Should().ContainSingle();
        (await FindAsync(user.Id)).FcmTokens.Should().ContainSingle();

        // The session is over.
        (await anonymous.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = user.RefreshToken }))
            .IsSuccessStatusCode.Should().BeFalse();
    }

    [DockerFact]
    public async Task Only_a_language_the_app_is_written_in_is_taken()
    {
        if (Skip)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);

        foreach (var language in new[] { "de", " ", "EN" })
        {
            var refused = await user.Client.PutAsJsonAsync("/api/users/me", new { language });
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, language);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("common.languageInvalid");
        }

        (await FindAsync(user.Id)).Language.Should().Be("az");

        using var anonymous = _fixture.CreateApiClient();
        (await anonymous.PutAsJsonAsync("/api/users/me", new { language = "en" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<User> FindAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MongoContext>().Users.Find(u => u.Id == id).SingleAsync();
    }
}
