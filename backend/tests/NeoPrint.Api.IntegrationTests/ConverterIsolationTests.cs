using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NeoPrint.Infrastructure.Conversion;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Conversions handed to the isolated converter through the spool
/// (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// The container's own isolation - no network, read-only root, cgroup limits -
/// is docker-compose.yml's and is pinned by the tests at the bottom; the
/// behaviour here is what runs inside it and in the API, with real child
/// processes and no Docker.
/// </remarks>
public sealed class ConverterIsolationTests : IDisposable
{
    private readonly string _spool;

    public ConverterIsolationTests()
    {
        _spool = Path.Combine(Path.GetTempPath(), $"neoprint-spool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_spool);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_spool, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    private static (string Program, string[] Arguments) Shell(string script)
        => OperatingSystem.IsWindows() ? ("cmd.exe", ["/c", script]) : ("/bin/sh", ["-c", script]);

    private static string ShellProgram => Shell(string.Empty).Program;

    private IsolatedProcessSandbox Client(int graceSeconds = 30) => new(
        Options.Create(new ConversionOptions
        {
            Mode = ConversionOptions.IsolatedMode,
            WorkRoot = _spool,
            IsolatedGraceSeconds = graceSeconds,
        }),
        NullLogger<IsolatedProcessSandbox>.Instance);

    private ConverterWorker Worker(params string[] programs) => new(
        _spool,
        new SandboxedProcess(NullLogger<SandboxedProcess>.Instance),
        new HashSet<string>(programs, StringComparer.Ordinal),
        NullLogger.Instance,
        poll: TimeSpan.FromMilliseconds(50));

    private string JobDirectory()
    {
        var directory = Path.Combine(_spool, $"neoprint-convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public async Task A_job_goes_through_the_spool_and_its_output_comes_back()
    {
        using var stop = new CancellationTokenSource();
        var worker = Worker(ShellProgram).RunAsync(stop.Token);
        var directory = JobDirectory();

        var (program, arguments) = Shell("echo converted> out.txt");

        var result = await Client().RunAsync(
            program, arguments, new SandboxLimits(TimeSpan.FromSeconds(20), 512L * 1024 * 1024, directory));

        await stop.CancelAsync();
        await worker;

        result.Succeeded.Should().BeTrue(result.StandardError);
        File.ReadAllText(Path.Combine(directory, "out.txt")).Trim().Should().Be("converted");

        // The worker left its heartbeat for the API's health check.
        File.Exists(Path.Combine(_spool, ConverterSpool.HeartbeatFile)).Should().BeTrue();
    }

    [Fact]
    public async Task A_program_off_the_allowlist_is_refused_rather_than_run()
    {
        // job.json is written by the API, but the container trusts only its
        // own list: a compromised or buggy caller cannot make it run a shell.
        using var stop = new CancellationTokenSource();
        var worker = Worker("gs").RunAsync(stop.Token);
        var directory = JobDirectory();

        var (program, arguments) = Shell("echo ran> ran.txt");

        var result = await Client().RunAsync(
            program, arguments, new SandboxLimits(TimeSpan.FromSeconds(20), 512L * 1024 * 1024, directory));

        await stop.CancelAsync();
        await worker;

        result.Outcome.Should().Be(SandboxOutcome.NotStarted);
        result.StandardError.Should().StartWith("Refused");
        File.Exists(Path.Combine(directory, "ran.txt")).Should().BeFalse();
    }

    [Fact]
    public void Limits_outside_the_allowed_range_are_refused()
    {
        var worker = Worker("gs");
        var directory = JobDirectory();

        worker.Refuse(directory, new ConverterJob("gs", [], 3600, 1024, 60)).Should().Be("limits out of range");
        worker.Refuse(directory, new ConverterJob("gs", [], 60, 1024, 60)).Should().BeNull();
        worker.Refuse(Path.GetTempPath(), new ConverterJob("gs", [], 60, 1024, 60)).Should().Be("the job is not in the spool");
    }

    [Fact]
    public async Task The_api_does_not_hand_a_directory_outside_the_spool_to_the_converter()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"not-spool-{Guid.NewGuid():N}");
        var (program, arguments) = Shell("exit 0");

        var run = () => Client().RunAsync(program, arguments, SandboxLimits.Default(outside));

        await run.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task No_converter_running_is_reported_as_not_started_after_the_grace_period()
    {
        // Nothing watches the spool: the upload must end as a failed
        // conversion, not hang the consumer forever.
        var directory = JobDirectory();
        var (program, arguments) = Shell("exit 0");

        var result = await Client(graceSeconds: 1).RunAsync(
            program, arguments, new SandboxLimits(TimeSpan.FromSeconds(1), 512L * 1024 * 1024, directory));

        result.Outcome.Should().Be(SandboxOutcome.NotStarted);
    }

    [Fact]
    public async Task Cancelling_on_the_api_side_stops_the_program_in_the_converter()
    {
        using var stop = new CancellationTokenSource();
        var worker = Worker(ShellProgram).RunAsync(stop.Token);
        var directory = JobDirectory();

        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", "sleep 30" });

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var started = DateTime.UtcNow;

        var run = () => Client().RunAsync(
            program, arguments, new SandboxLimits(TimeSpan.FromSeconds(60), 512L * 1024 * 1024, directory), cancel.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();

        // The worker saw the cancel marker and ended the program well before
        // its own sixty seconds.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(Path.Combine(directory, ConverterSpool.ResultFile)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        File.Exists(Path.Combine(directory, ConverterSpool.ResultFile)).Should().BeTrue();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20));

        await stop.CancelAsync();
        await worker;
    }

    [Fact]
    public void Outside_development_conversions_must_be_isolated()
    {
        var local = new ConversionOptions { Mode = ConversionOptions.LocalMode };
        var isolated = new ConversionOptions { Mode = ConversionOptions.IsolatedMode, WorkRoot = "/spool" };

        ((Action)(() => local.EnsureAllowedIn(isDevelopment: false))).Should().Throw<InvalidOperationException>();
        ((Action)(() => new ConversionOptions().EnsureAllowedIn(isDevelopment: false))).Should().Throw<InvalidOperationException>();
        ((Action)(() => new ConversionOptions { Mode = ConversionOptions.IsolatedMode }.EnsureAllowedIn(false)))
            .Should().Throw<InvalidOperationException>("the spool is required");

        ((Action)(() => isolated.EnsureAllowedIn(isDevelopment: false))).Should().NotThrow();
        ((Action)(() => local.EnsureAllowedIn(isDevelopment: true))).Should().NotThrow();
    }

    // -----------------------------------------------------------------------
    // The container's isolation, as docker-compose.yml declares it
    // -----------------------------------------------------------------------

    private static string ComposeService(string name)
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docker-compose.yml"));
        var start = Array.FindIndex(lines, line => line == $"  {name}:");
        start.Should().BeGreaterThan(0, $"docker-compose.yml has a {name} service");

        var end = Array.FindIndex(lines, start + 1, line => line.Length > 2 && line[0] == ' ' && line[1] == ' ' && line[2] != ' ' && line[2] != '#');
        return string.Join('\n', lines[start..(end < 0 ? lines.Length : end)]);
    }

    [Fact]
    public void The_converter_container_has_no_network_no_secrets_and_capped_resources()
    {
        var converter = ComposeService("converter");

        converter.Should().Contain("network_mode: none", "a converter exploit must have nowhere to connect to");
        converter.Should().Contain("read_only: true");
        converter.Should().Contain("- ALL", "every capability dropped");
        converter.Should().Contain("no-new-privileges:true");
        converter.Should().Contain("cpus:").And.Contain("mem_limit:").And.Contain("pids_limit:");
        converter.Should().Contain("--converter-worker");

        // Nothing to authenticate with: no seed file, no OpenBao credentials.
        converter.Should().NotContain("env_file").And.NotContain(".openbao").And.NotContain("BAO_");
        converter.Should().NotContain("ports:");
    }

    [Fact]
    public void The_api_sends_conversions_to_the_converter()
    {
        var api = ComposeService("api");

        api.Should().Contain("Conversion__Mode: Isolated");
        api.Should().Contain("Conversion__WorkRoot: /spool");
        api.Should().Contain("conversion-spool:/spool");
    }

    [Fact]
    public void The_spool_lives_in_memory()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker-compose.yml"));
        var spool = compose[compose.IndexOf("  conversion-spool:\n", StringComparison.Ordinal)..];

        spool.Should().Contain("type: tmpfs", "a document is never written to the host's disk");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
