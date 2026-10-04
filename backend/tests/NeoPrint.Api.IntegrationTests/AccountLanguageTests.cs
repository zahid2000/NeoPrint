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
