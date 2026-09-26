namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The test suite and the development environment run the same object store.
/// </summary>
/// <remarks>
/// The storage tests prove encryption, rotation and deletion against
/// CloudServer. That proof only says something about the development
/// environment if it is the same CloudServer, and the two are pinned in two
/// different files. No Docker: this reads them.
/// </remarks>
public sealed class ComposeParityTests
{
    [Fact]
    public void The_fixture_runs_the_image_docker_compose_pins()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker-compose.yml"));

        compose.Should().Contain(
            $"image: {NeoPrintApiFixture.CloudServerImage}",
            "docker-compose.yml and the test fixture have to run the same CloudServer build");
    }

    [Fact]
    public void Every_cloudserver_in_compose_is_the_same_build()
    {
        var images = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docker-compose.yml"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("image: ghcr.io/scality/cloudserver", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        // zenko, zenko-secondary and zenko-init: a migration between two
        // different builds would test the builds, not the migration.
        images.Should().ContainSingle();
    }

    [Fact]
    public void The_cloudserver_image_is_pinned_by_digest()
    {
        // A tag can be moved to different content; a digest cannot.
        NeoPrintApiFixture.CloudServerImage.Should().Contain("@sha256:");
    }

    [Fact]
    public void No_minio_image_is_left_in_compose()
    {
        // Its images stopped being anonymously pullable in September 2026, so
        // one left behind breaks docker compose up on a clean machine
        // (docs/DECISIONS.md).
        var compose = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docker-compose.yml"));

        compose.Where(line => line.TrimStart().StartsWith("image:", StringComparison.Ordinal))
            .Should().NotContain(line => line.Contains("minio", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root has to be findable from the test output directory");

        return directory!.FullName;
    }
}
