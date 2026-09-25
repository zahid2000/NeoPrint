using System.Net;
using System.Net.Http;

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

    /// <summary>
    /// The registration and the usage site both have to sit inside an
    /// <c>IsDevelopment()</c> branch.
    /// </summary>
    /// <remarks>
    /// A source check rather than a request, because the alternative is booting
    /// the API in Production, which by design refuses to start without a real
    /// secret store. This keeps the guard honest for whoever edits
    /// <c>Program.cs</c> next.
    /// </remarks>
    [Fact]
    public void Cors_is_registered_only_for_development()
    {
        var program = File.ReadAllText(ProgramFile());
        var lines = program.Split('\n');

        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
        {
            if (!line.Contains("AddCors(", StringComparison.Ordinal)
                && !line.Contains("UseCors(", StringComparison.Ordinal))
            {
                continue;
            }

            // Walk back to the nearest enclosing condition. Both call sites are
            // one block deep, so the guard is within a few lines above.
            var guarded = lines
                .Take(index)
                .Reverse()
                .Take(20)
                .Any(earlier => earlier.Contains("IsDevelopment()", StringComparison.Ordinal));

            guarded.Should().BeTrue(
                $"line {index + 1} ({line.Trim()}) must be reachable only in Development");
        }

        program.Should().Contain("AddCors(", "the development policy is what this test guards");
        program.Should().Contain("UseCors(");
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
