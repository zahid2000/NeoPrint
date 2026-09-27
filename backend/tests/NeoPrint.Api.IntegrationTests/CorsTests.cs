using System.Net;
using System.Net.Http;

using Microsoft.Extensions.Configuration;

using NeoPrint.Api.Infrastructure;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Cross-origin access: on for a browser on the developer's own machine, off
/// everywhere else.
/// </summary>
/// <remarks>
/// The panel is same-origin by design (plan section 8A.1), so the only client
/// that needs this is the Flutter app under <c>flutter run -d chrome</c>. That
/// convenience must not become a production hole, which is what
/// <see cref="Cors_is_registered_only_for_development"/> is for.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CorsTests
{
    private readonly NeoPrintApiFixture _fixture;

    public CorsTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task A_browser_on_this_machine_is_allowed_through()
    {
        using var client = _fixture.CreateApiClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/register");
        preflight.Headers.Add("Origin", "http://localhost:63412");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");

        var response = await client.SendAsync(preflight);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin")
            .Should().Contain("http://localhost:63412");
    }

    [DockerFact]
    public async Task Any_other_origin_is_not()
    {
        using var client = _fixture.CreateApiClient();

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/register");
        preflight.Headers.Add("Origin", "https://neoprint.attacker.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(preflight);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse(
            "a page the developer did not serve must not be able to read the API's answers");
    }

    [Theory]
    [InlineData("http://localhost:4200")]
    [InlineData("http://localhost")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://[::1]:8080")]
    [InlineData("http://10.0.2.2:5080")]
    public void Loopback_origins_are_recognised(string origin)
        => DevelopmentCors.IsLoopbackOrigin(origin).Should().BeTrue();

    [Theory]
    [InlineData("https://neoprint.az")]
    [InlineData("http://localhost.attacker.example")]
    [InlineData("http://notlocalhost")]
    [InlineData("file:///C:/tmp/index.html")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("   ")]
    public void Everything_else_is_not(string origin)
        => DevelopmentCors.IsLoopbackOrigin(origin).Should().BeFalse();

    private static CorsOrigins Origins(bool development, params string[] configured)
    {
        var settings = configured
            .Select((origin, index) => new KeyValuePair<string, string?>($"{CorsOrigins.SectionName}:{index}", origin));

        return CorsOrigins.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            development);
    }

    [Fact]
    public void Outside_development_only_the_configured_origins_are_allowed()
    {
        var origins = Origins(development: false, "https://app.neoprint.az");

        origins.IsAllowed("https://app.neoprint.az").Should().BeTrue();

        // No loopback, no neighbour, no downgrade.
        origins.IsAllowed("http://localhost:4200").Should().BeFalse();
        origins.IsAllowed("https://evil.neoprint.az").Should().BeFalse();
        origins.IsAllowed("http://app.neoprint.az").Should().BeFalse();
    }

    [Fact]
    public void With_nothing_configured_a_deployment_allows_no_origin_at_all()
    {
        var origins = Origins(development: false);

        origins.IsAllowed("http://localhost:4200").Should().BeFalse();
        origins.IsAllowed("https://app.neoprint.az").Should().BeFalse();
    }

    [Fact]
    public void Development_allows_loopback_and_the_configured_origins()
    {
        var origins = Origins(development: true, "http://192.168.1.50:8080");

        origins.IsAllowed("http://localhost:63412").Should().BeTrue();
        origins.IsAllowed("http://192.168.1.50:8080").Should().BeTrue();
        origins.IsAllowed("https://neoprint.attacker.example").Should().BeFalse();
    }

    [Theory]
    [InlineData("https://*.neoprint.az")]
    [InlineData("https://app.neoprint.az/path")]
    [InlineData("app.neoprint.az")]
    [InlineData("http://app.neoprint.az")]
    public void A_loose_origin_stops_a_deployment_from_starting(string origin)
    {
        var configure = () => Origins(development: false, origin);

        configure.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_policy_is_the_one_the_pipeline_uses()
    {
        var program = File.ReadAllText(ProgramFile());

        program.Should().Contain("AddCors(").And.Contain("CorsOrigins.PolicyName");
        program.Should().Contain("app.UseCors(CorsOrigins.PolicyName)");
    }

    private static string ProgramFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root has to be findable from the test output directory");

        return Path.Combine(directory!.FullName, "backend", "src", "NeoPrint.Api", "Program.cs");
    }
}
