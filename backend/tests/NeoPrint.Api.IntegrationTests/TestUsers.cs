using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>A signed-in mobile app user of its own, so tests do not share a wallet.</summary>
internal sealed class TestUser : IDisposable
{
    public required HttpClient Client { get; init; }

    public required Guid Id { get; init; }

    public required string Email { get; init; }

    public string RefreshToken { get; init; } = string.Empty;

    public void Dispose() => Client.Dispose();
}

internal static class TestUsers
{
    public const string Password = "Wallet!Test2026";

    /// <summary>Registers, confirms with the emailed code and signs in (plan section 9.0 flow for the app).</summary>
    public static async Task<TestUser> ConfirmedAsync(NeoPrintApiFixture fixture)
    {
        var client = fixture.CreateApiClient();
        var email = $"wallet-{Guid.NewGuid():N}@neoprint.test";

        var privacy = await client.GetFromJsonAsync<JsonElement>("/api/legal/Privacy?lang=az");
        var terms = await client.GetFromJsonAsync<JsonElement>("/api/legal/Terms?lang=az");

        var registered = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = Password,
            language = "az",
            consents = new[]
            {
                new { docType = "Privacy", version = privacy.GetProperty("version").GetString() },
                new { docType = "Terms", version = terms.GetProperty("version").GetString() },
            },
        });

        registered.StatusCode.Should().Be(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());

        var confirmed = await client.PostAsJsonAsync("/api/auth/confirm-email", new { email, code = fixture.Emails.CodeFor(email) });
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());

        var session = await confirmed.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());

        return new TestUser
        {
            Client = client,
            Id = session.GetProperty("user").GetProperty("id").GetGuid(),
            Email = email,
            RefreshToken = session.GetProperty("refreshToken").GetString()!,
        };
    }
}
