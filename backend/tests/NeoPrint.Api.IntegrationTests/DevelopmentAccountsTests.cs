using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Infrastructure.Persistence;
using NeoPrint.Infrastructure.Seeding;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The ready-made accounts docs/DEV_ENVIRONMENT.md lists: one per admin role
/// and a confirmed mobile user, all on <c>NEOPRINT_DEV_TEST_PASSWORD</c>.
/// </summary>
/// <remarks>
/// The fixture runs the API in Development, which is the only environment the
/// development seeder runs in (Program.cs). The runbook promises these
/// accounts work as written, so the test signs into each one the way a person
/// would.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class DevelopmentAccountsTests
{
    private readonly NeoPrintApiFixture _fixture;

    public DevelopmentAccountsTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task Every_admin_test_account_signs_in_with_its_role_and_no_password_change()
    {
        foreach (var (email, _, role) in DevelopmentSeeder.TestAdmins)
        {
            using var client = _fixture.CreateApiClient();

            var login = await client.PostAsJsonAsync(
                "/api/auth/admin/login", new { email, password = NeoPrintApiFixture.DevTestPassword });

            login.StatusCode.Should().Be(HttpStatusCode.OK, $"{email}: {await login.Content.ReadAsStringAsync()}");

            var admin = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("admin");

            admin.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse(email);
            admin.GetProperty("roles").EnumerateArray().Select(value => value.GetString())
                .Should().Contain(role, email);
        }
    }

    [DockerFact]
    public async Task The_mobile_test_user_is_confirmed_and_has_nothing_left_to_accept()
    {
        using var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = DevelopmentSeeder.TestUserEmail,
            password = NeoPrintApiFixture.DevTestPassword,
            deviceId = Guid.NewGuid().ToString("N"),
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        var user = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("user");

        user.GetProperty("emailConfirmed").GetBoolean().Should().BeTrue();

        // Both documents were accepted when the account was seeded, so it
        // signs in to the home screen. Checked on the record rather than on
        // pendingConsents, which other tests change by publishing new versions.
        using var scope = _fixture.Services.CreateScope();
        var accepted = await scope.ServiceProvider
            .GetRequiredService<NeoPrint.Application.Abstractions.ILegalDocumentRepository>()
            .GetAcceptedVersionsAsync(user.GetProperty("id").GetGuid());

        accepted.Keys.Should().Contain(
            [NeoPrint.Domain.Users.LegalDocumentType.Privacy, NeoPrint.Domain.Users.LegalDocumentType.Terms]);
    }

    [DockerFact]
    public async Task A_test_account_is_not_reset_by_the_next_start()
    {
        // Someone who changes a test account's password on purpose keeps it:
        // the seeder only creates what is missing.
        using var scope = _fixture.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        async Task<List<string>> HashesAsync()
        {
            var hashes = new List<string>();
            foreach (var (email, _, _) in DevelopmentSeeder.TestAdmins)
            {
                var found = await context.AdminUsers.Find(admin => admin.Email == email).ToListAsync();
                found.Should().ContainSingle(email);
                hashes.Add(found[0].PasswordHash);
            }

            return hashes;
        }

        var before = await HashesAsync();

        await seeder.SeedAsync();

        (await HashesAsync()).Should().Equal(before);
    }
}
