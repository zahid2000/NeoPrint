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
        var sources = Directory.EnumerateFiles(SourceDirectory(), "*.cs", SearchOption.TopDirectoryOnly).ToList();
        sources.Should().Contain(path => Path.GetFileName(path) == nameof(NeoPrintApiFixture) + ".cs", "the rule reads the real test sources, not an empty folder");

        var offenders = sources
            .Where(path => Path.GetFileName(path) is not (nameof(NeoPrintApiFixture) + ".cs") and not (nameof(TestHostRulesTests) + ".cs"))
            .Where(path => File.ReadAllText(path).Contains(".WithWebHostBuilder(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        offenders.Should().BeEmpty("a second host must not run the message bus; use _fixture.WithHttpOnlyHost(...)");
    }

    /// <summary>
    /// The project folder, found upward from the test assembly. Not
    /// [CallerFilePath]: a CI build maps source paths to /_/ (ContinuousIntegrationBuild),
    /// and that folder does not exist on the runner (T-128).
    /// </summary>
    private static string SourceDirectory()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "NeoPrint.Api.IntegrationTests.csproj")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("NeoPrint.Api.IntegrationTests.csproj was not found above " + AppContext.BaseDirectory);
    }
}
