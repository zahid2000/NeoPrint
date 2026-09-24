using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Register, confirm, sign in (plan section 9.0, step 1 Definition of Done).
/// </summary>
/// <remarks>
/// The flow the mobile screens drive. It had no test of its own, which is how
/// the bypass in <c>Confirming_an_address_twice_still_needs_the_right_code</c>
/// survived: an already confirmed account was handed a session without the
/// code being looked at, so knowing a registered address was enough to sign in
/// as its owner.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class EmailConfirmationTests
{
    private readonly NeoPrintApiFixture _fixture;

    public EmailConfirmationTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerFact]
    public async Task A_new_account_confirms_with_the_code_it_was_emailed_and_is_signed_in()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var email = Address();

        var registered = await RegisterAsync(client, email, Password);

        registered.StatusCode.Should().Be(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());

        var body = await registered.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("confirmationRequired").GetBoolean().Should().BeTrue();

        // Registration hands out no session: the address is unproven.
        body.TryGetProperty("accessToken", out _).Should().BeFalse();

        var code = _fixture.Emails.CodeFor(email);

        var confirmed = await client.PostAsJsonAsync("/api/auth/confirm-email", new { email, code });

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());

        var session = await confirmed.Content.ReadFromJsonAsync<JsonElement>();

        session.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task An_unconfirmed_account_cannot_sign_in()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var email = Address();

        await RegisterAsync(client, email, Password);

        var signIn = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });

        signIn.IsSuccessStatusCode.Should().BeFalse();
    }

    [DockerFact]
    public async Task A_wrong_code_is_refused_and_does_not_confirm_the_address()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var email = Address();

        await RegisterAsync(client, email, Password);

        var wrong = await client.PostAsJsonAsync("/api/auth/confirm-email", new { email, code = "000000" });

        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // And the real code still works afterwards, because a wrong guess must
        // not burn it - only five of them do.
        var code = _fixture.Emails.CodeFor(email);

        var confirmed = await client.PostAsJsonAsync("/api/auth/confirm-email", new { email, code });

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
    }

    [DockerFact]
    public async Task Confirming_an_address_twice_still_needs_the_right_code()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var email = Address();

        await RegisterAsync(client, email, Password);

        var code = _fixture.Emails.CodeFor(email);

        (await client.PostAsJsonAsync("/api/auth/confirm-email", new { email, code }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The same request again - a client whose response was lost - gets the
        // same answer (docs/IDEMPOTENCY.md).
        var replay = await client.PostAsJsonAsync("/api/auth/confirm-email", new { email, code });

        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());

        var session = await replay.Content.ReadFromJsonAsync<JsonElement>();

        session.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();

        // But a wrong code is refused even though the address is confirmed.
        // Without this, knowing a registered address would be enough to be
        // handed a session, with no password anywhere in the exchange.
        var guessed = await client.PostAsJsonAsync(
            "/api/auth/confirm-email", new { email, code = "000000" });

        guessed.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "a confirmed address must not turn confirm-email into a way in");

        (await guessed.Content.ReadAsStringAsync()).Should().NotContain("accessToken");
    }

    [DockerFact]
    public async Task An_unknown_address_is_refused_the_same_way_as_a_wrong_code()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var unknown = await client.PostAsJsonAsync(
            "/api/auth/confirm-email", new { email = Address(), code = "000000" });

        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // One message for both, so this cannot be used to discover which
        // addresses are registered.
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("Kod yanlışdır");
    }

    [DockerFact]
    public async Task The_confirmation_email_carries_a_code_and_never_the_password()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var email = Address();

        await RegisterAsync(client, email, Password);

        var message = _fixture.Emails.Latest(email);

        message.Should().NotBeNull();
        message!.Subject.Should().NotBeNullOrWhiteSpace();
        message.HtmlBody.Should().NotContain(Password);
        _fixture.Emails.CodeFor(email).Should().HaveLength(6);
    }

    private const string Password = "DodSignupPassword!2026";

    private static string Address() => $"dod.signup.{Guid.NewGuid():N}@neoprint.test";

    private static async Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string email, string password)
    {
        var privacy = await client.GetFromJsonAsync<JsonElement>("/api/legal/Privacy?lang=az");
        var terms = await client.GetFromJsonAsync<JsonElement>("/api/legal/Terms?lang=az");

        return await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            language = "az",
            consents = new[]
            {
                new { docType = "Privacy", version = privacy.GetProperty("version").GetString() },
                new { docType = "Terms", version = terms.GetProperty("version").GetString() },
            },
        });
    }
}
