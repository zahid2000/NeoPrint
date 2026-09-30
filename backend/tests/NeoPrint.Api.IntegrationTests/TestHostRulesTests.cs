using System.Runtime.CompilerServices;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A second API host made with <c>WithWebHostBuilder</c> joins the fixture's
/// RabbitMQ queues and, once disposed, loses the messages it takes (a
/// cancelled job's refund was never made). Tests use
/// <see cref="NeoPrintApiFixture.WithHttpOnlyHost"/> instead.
/// </summary>
public sealed class TestHostRulesTests
{
    [Fact]
    public void Only_the_fixture_builds_a_second_host_directly()
    {
        var offenders = Directory
            .EnumerateFiles(SourceDirectory(), "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path) is not (nameof(NeoPrintApiFixture) + ".cs") and not (nameof(TestHostRulesTests) + ".cs"))
            .Where(path => File.ReadAllText(path).Contains(".WithWebHostBuilder(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("a second host must not run the message bus; use _fixture.WithHttpOnlyHost(...)");
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
