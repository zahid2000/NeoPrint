using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// What the API says, it says in the caller's language; what it mails, in the
/// recipient's (plan section 2.1; docs/TASKS.md T-043).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MessageLanguageTests
{
    private readonly NeoPrintApiFixture _fixture;

    public MessageLanguageTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    [DockerTheory]
    [InlineData(null, "Email və ya şifrə yanlışdır.", "Sorğu yoxlamadan keçmədi")]
    [InlineData("en-US,en;q=0.9", "The email or the password is wrong.", "Validation failed")]
    [InlineData("ru", "Неверный адрес электронной почты или пароль.", "Запрос не прошёл проверку")]
    [InlineData("de-DE,ru;q=0.5", "Неверный адрес электронной почты или пароль.", "Запрос не прошёл проверку")]
    [InlineData("fr", "Email və ya şifrə yanlışdır.", "Sorğu yoxlamadan keçmədi")]
    public async Task A_refusal_reads_in_the_language_the_client_asked_for(
        string? acceptLanguage, string expectedError, string expectedTitle)
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = $"nobody.{Guid.NewGuid():N}@neoprint.test", password = "Wrong!2026x" }),
        };

        if (acceptLanguage is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        problem.GetProperty("title").GetString().Should().Be(expectedTitle);
        problem.GetProperty("errors").GetProperty("").EnumerateArray().Single().GetString().Should().Be(expectedError);

        // The key travels too, so a client can react without reading the text.
        problem.GetProperty("code").GetString().Should().Be("auth.credentialsInvalid");
    }

    [DockerFact]
    public async Task A_rule_broken_in_a_handler_reads_in_the_caller_s_language_too()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new { email = "not-an-address", password = "x", language = "ru" }),
        };

        request.Headers.TryAddWithoutValidation("Accept-Language", "en");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("The email address is not valid.")
            .And.Contain("The password must be at least")
            .And.NotContain("Şifrə");
    }

    [DockerFact]
    public async Task The_confirmation_email_is_in_the_new_user_s_language_whatever_the_request_was_in()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var email = $"lang.{Guid.NewGuid():N}@neoprint.test";
        var privacy = await client.GetFromJsonAsync<JsonElement>("/api/legal/Privacy?lang=az");
        var terms = await client.GetFromJsonAsync<JsonElement>("/api/legal/Terms?lang=az");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/register")
        {
            Content = JsonContent.Create(new
            {
                email,
                password = "LanguagePassword!2026",
                language = "ru",
                consents = new[]
                {
                    new { docType = "Privacy", version = privacy.GetProperty("version").GetString() },
                    new { docType = "Terms", version = terms.GetProperty("version").GetString() },
                },
            }),
        };

        // The phone's interface was English at the moment of sign-up; the
        // account says Russian, and so does its mail.
        request.Headers.TryAddWithoutValidation("Accept-Language", "en");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var mail = _fixture.Emails.Latest(email);

        mail.Should().NotBeNull();
        mail!.Subject.Should().Be("NeoPrint — подтверждение электронной почты");
        mail.HtmlBody.Should().Contain("Код действует");
        _fixture.Emails.CodeFor(email).Should().MatchRegex("^[0-9]{6}$");
    }
}
