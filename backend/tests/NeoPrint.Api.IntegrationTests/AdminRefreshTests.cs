using System.Net;
using System.Net.Http.Json;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// What the admin refresh endpoint answers when it will not issue a token.
/// </summary>
/// <remarks>
/// Every refusal is a 401 and every refusal says the same thing. The status
/// matters because the panel signs the person in again on a 401 and retries a
/// 400; the wording matters because a caller must not be able to tell an
/// unknown token from an expired one or from a deactivated account.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AdminRefreshTests
{
    private readonly NeoPrintApiFixture _fixture;

    public AdminRefreshTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task No_cookie_at_all_is_a_401()
    {
        using var client = _fixture.CreateApiClient();

        var response = await client.PostAsync("/api/auth/admin/refresh", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task A_token_the_server_never_issued_is_a_401()
    {
        using var handler = new HttpClientHandler { UseCookies = false };
        using var client = _fixture.CreateApiClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/auth/admin/refresh");

        request.Headers.Add(
            "Cookie",
            "neoprint_admin_rt=this-token-was-never-issued-by-anyone");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Not a validation problem: no per-field errors, so a client cannot
        // read this as "the field was wrong, fix it and retry".
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("refreshToken");
    }

    [DockerFact]
    public async Task A_token_that_was_already_used_is_a_401()
    {
        using var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync(
            "/api/auth/admin/login",
            new
            {
                email = NeoPrintApiFixture.SeedAdminEmail,
                password = NeoPrintApiFixture.SeedAdminPassword,
            });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        // The cookie the login set is held by the client's own cookie
        // container, so it travels on its own. Capture it before it rotates.
        var cookie = login.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.First().Split(';')[0]
            : null;

        cookie.Should().NotBeNull("the login is supposed to set the refresh cookie");

        var first = await client.PostAsync("/api/auth/admin/refresh", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        // Present the first token again. Rotation means it is spent, and
        // replaying it revokes the whole family (plan section 2.8).
        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/auth/admin/refresh");
        replay.Headers.Add("Cookie", cookie!);

        using var other = _fixture.CreateApiClient();
        var response = await other.SendAsync(replay);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
