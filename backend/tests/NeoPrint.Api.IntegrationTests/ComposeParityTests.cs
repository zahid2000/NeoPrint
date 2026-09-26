namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The test suite and the development environment run the same object store.
/// </summary>
/// <remarks>
/// The storage tests prove encryption, rotation, migration and S3
/// compatibility against RustFS. That proof only says something about the
/// development environment if it is the same RustFS, and the two are pinned in
/// two different files. No Docker: this reads them.
/// </remarks>
public sealed class ComposeParityTests
{
    [Fact]
    public void The_fixture_runs_the_image_docker_compose_pins()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker-compose.yml"));

        compose.Should().Contain(
            $"image: {NeoPrintApiFixture.RustFsImage}",
            "docker-compose.yml and the test fixture have to run the same RustFS build");
    }

    [Fact]
    public void Every_rustfs_in_compose_is_the_same_build()
    {
        var images = ImageLines()
            .Where(line => line.StartsWith("image: rustfs/rustfs", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        // rustfs, rustfs-secondary and rustfs-init: a migration between two
        // different builds would test the builds, not the migration.
        images.Should().ContainSingle();
    }

    [Fact]
    public void The_rustfs_image_is_pinned_by_digest()
    {
        // A tag can be moved to different content; a digest cannot.
        NeoPrintApiFixture.RustFsImage.Should().Contain("@sha256:");
    }

    [Fact]
    public void Neither_minio_nor_cloudserver_is_left_in_compose()
    {
        // MinIO's images stopped being anonymously pullable in September 2026,
        // so one left behind breaks docker compose up on a clean machine; Zenko
        // CloudServer was dropped for RustFS (docs/DECISIONS.md).
        ImageLines().Should().NotContain(line =>
            line.Contains("minio", StringComparison.OrdinalIgnoreCase)
            || line.Contains("cloudserver", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> ImageLines() =>
        File.ReadAllLines(Path.Combine(RepositoryRoot(), "docker-compose.yml"))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("image:", StringComparison.Ordinal));

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
