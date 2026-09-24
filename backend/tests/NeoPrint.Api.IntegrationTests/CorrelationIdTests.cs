using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Common;
using NeoPrint.Infrastructure.Observability;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Every request carries a correlation id, and the caller gets it back
/// (<c>docs/OBSERVABILITY.md</c>).
/// </summary>
/// <remarks>
/// One print transaction has to be traceable from the API to the printer and on
/// to the refund. That starts here: an id on the request, echoed to the caller,
/// and in scope for every log line the request produces.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CorrelationIdTests
{
    private readonly NeoPrintApiFixture _fixture;

    public CorrelationIdTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    // -----------------------------------------------------------------------
    // Sanitising an incoming id - no container needed
    // -----------------------------------------------------------------------

    [Fact]
    public void An_id_the_caller_supplied_is_kept_when_it_is_plain()
    {
        CorrelationContext.Sanitize("abc-123_45.6").Should().Be("abc-123_45.6");
    }

    [Fact]
    public void A_newline_cannot_be_smuggled_into_the_log()
    {
        // The value ends up in log lines. A newline would let a caller forge a
        // second log entry, so anything but identifier characters is dropped
        // (docs/SECURITY_THREAT_MODEL.md).
        var sanitized = CorrelationContext.Sanitize("abc\r\nlevel=Error message=forged");

        sanitized.Should().NotContain("\n");
        sanitized.Should().NotContain("\r");
        sanitized.Should().NotContain(" ");
        sanitized.Should().Be("abclevelErrormessageforged");
    }

    [Fact]
    public void A_long_id_is_cut_to_a_sane_length()
    {
        CorrelationContext.Sanitize(new string('a', 500)).Should().HaveLength(128);
    }

    [Fact]
    public void An_id_with_nothing_usable_in_it_is_replaced()
    {
        var sanitized = CorrelationContext.Sanitize("<<< >>>");

        // Rather than logging an empty field, which correlates to nothing.
        sanitized.Should().NotBeEmpty();
        sanitized.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void A_context_nobody_set_still_has_an_id()
    {
        new CorrelationContext().CorrelationId.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Setting_an_empty_id_does_not_wipe_the_one_in_use()
    {
        var context = new CorrelationContext();
        var original = context.CorrelationId;

        context.Set(string.Empty);
        context.Set("   ");

        context.CorrelationId.Should().Be(original);
    }

    // -----------------------------------------------------------------------
    // Over HTTP
    // -----------------------------------------------------------------------

    [DockerFact]
    public async Task A_request_without_an_id_is_given_one()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        var response = await client.GetAsync("/health/live");

        var returned = response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single();

        returned.Should().NotBeNullOrWhiteSpace();
    }

    [DockerFact]
    public async Task An_id_the_caller_sent_is_echoed_back()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        const string supplied = "mobile-session-42";

        client.DefaultRequestHeaders.Add(CorrelationHeaders.CorrelationId, supplied);

        var response = await client.GetAsync("/health/live");

        // The mobile app and the kiosk send their own id, so a failure a user
        // reports can be found on the server side.
        response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single().Should().Be(supplied);
    }

    [DockerFact]
    public async Task A_hostile_id_is_not_echoed_back_as_sent()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        // A header value is caller-controlled, and it is echoed into a response
        // header: a newline there would split the response.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            CorrelationHeaders.CorrelationId, "abc\u0000def<script>");

        var response = await client.GetAsync("/health/live");

        var returned = response.Headers.GetValues(CorrelationHeaders.CorrelationId).Single();

        returned.Should().Be("abcdefscript");
    }

    [DockerFact]
    public async Task A_refused_request_reports_its_correlation_id_in_the_problem()
    {
        if (Skip) { return; }

        using var client = _fixture.CreateApiClient();

        const string supplied = "trace-a-failure";

        client.DefaultRequestHeaders.Add(CorrelationHeaders.CorrelationId, supplied);

        var response = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = "admin@neoprint.test",
            password = "definitely-not-the-password",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        // So somebody reporting "it failed" can quote an id that finds the
        // request in the logs.
        problem.GetProperty("correlationId").GetString().Should().Be(supplied);
    }

    [DockerFact]
    public async Task An_audited_action_records_the_correlation_id_of_its_request()
    {
        if (Skip) { return; }

        using var admin = await SignedInSuperAdminAsync();

        var correlationId = $"audit-{Guid.NewGuid():n}";
        admin.DefaultRequestHeaders.Add(CorrelationHeaders.CorrelationId, correlationId);

        var created = await admin.PostAsJsonAsync("/api/admin/roles", new
        {
            name = $"Correlated role {Guid.NewGuid():N}",
            description = "Created to check the audit trail.",
            permissions = new[] { "devices.view" },
        });

        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        using var scope = _fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var entries = await context.AuditLogs
            .Find(entry => entry.CorrelationId == correlationId)
            .ToListAsync();

        entries.Should().HaveCount(1);
        entries[0].Action.Should().Be("roles.create");
        entries[0].ActorType.Should().Be(ActorType.Admin);
    }

    private async Task<HttpClient> SignedInSuperAdminAsync()
    {
        var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var body = await login.Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", body.GetProperty("accessToken").GetString());

        return client;
    }
}
