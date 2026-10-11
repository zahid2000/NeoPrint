using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NeoPrint.Converter;
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

    private ConverterWorker Worker(params string[] programs) => Worker(2, programs);

    private ConverterWorker Worker(int parallelJobs, params string[] programs) => new(
        _spool,
        new SandboxedProcess(NullLogger<SandboxedProcess>.Instance),
        new HashSet<string>(programs, StringComparer.Ordinal),
        NullLogger.Instance,
        parallelJobs,
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
    public async Task A_directory_carries_one_job_after_another()
    {
        // A conversion and then the page count of its result share a
        // directory; the second job must not be answered with the first one's
        // result, nor left unclaimed because the first one's claim is there.
        using var stop = new CancellationTokenSource();
        var worker = Worker(ShellProgram).RunAsync(stop.Token);
        var directory = JobDirectory();
        var limits = new SandboxLimits(TimeSpan.FromSeconds(20), 512L * 1024 * 1024, directory);

        var (program, first) = Shell("exit 0");
        var (_, second) = Shell("exit 3");

        var firstResult = await Client().RunAsync(program, first, limits);
        var secondResult = await Client().RunAsync(program, second, limits);

        await stop.CancelAsync();
        await worker;

        firstResult.Succeeded.Should().BeTrue(firstResult.StandardError);
        secondResult.Outcome.Should().Be(SandboxOutcome.Failed);
        secondResult.ExitCode.Should().Be(3);
    }

    [Fact]
    public async Task A_request_someone_is_waiting_on_is_told_at_once_that_no_converter_is_running()
    {
        // A preview must not hang for its whole timeout when the converter
        // container is down: the answer is "try again shortly", straight away.
        var directory = JobDirectory();
        var (program, arguments) = Shell("exit 0");
        var started = DateTime.UtcNow;

        var result = await Client().RunAsync(
            program,
            arguments,
            new SandboxLimits(TimeSpan.FromSeconds(30), 512L * 1024 * 1024, directory) { FailFast = true });

        result.Outcome.Should().Be(SandboxOutcome.NotStarted);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        File.Exists(Path.Combine(directory, ConverterSpool.JobFile)).Should().BeFalse("nothing was queued for a converter that is not there");
    }

    [Fact]
    public async Task A_short_tool_job_does_not_queue_behind_a_long_conversion()
    {
        // One conversion slot, taken for fifteen seconds by an "office
        // document"; a page count asked for meanwhile has slots of its own.
        using var stop = new CancellationTokenSource();
        var worker = Worker(1, ShellProgram, ToolContract.Program).RunAsync(stop.Token);

        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping -n 16 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", "sleep 15" });

        var slowDirectory = JobDirectory();
        var slow = Client().RunAsync(
            program, arguments, new SandboxLimits(TimeSpan.FromSeconds(60), 512L * 1024 * 1024, slowDirectory));

        await Task.Delay(TimeSpan.FromSeconds(1));

        // No request file: the tool answers "usage" and exits - a real run of
        // the real tool that needs no document.
        var quick = await Client().RunAsync(
            ToolContract.Program,
            [ToolContract.PdfInfo],
            new SandboxLimits(TimeSpan.FromSeconds(30), ConverterTools.MemoryLimitBytes, JobDirectory()));

        quick.Outcome.Should().Be(SandboxOutcome.Failed, quick.StandardError);
        quick.ExitCode.Should().Be(64);
        slow.IsCompleted.Should().BeFalse("the tool job was answered while the conversion was still running");

        File.WriteAllText(Path.Combine(slowDirectory, ConverterSpool.CancelFile), string.Empty);
        await slow;
        await stop.CancelAsync();
        await worker;
    }

    [Fact]
    public void The_converter_tool_is_asked_only_for_its_own_operations()
    {
        var worker = Worker(ToolContract.Program);
        var directory = JobDirectory();

        ConverterJob Tool(params string[] arguments) => new(ToolContract.Program, arguments, 30, 1024, 60);

        worker.Refuse(directory, Tool(ToolContract.PdfRender)).Should().BeNull();
        worker.Refuse(directory, Tool("run-anything")).Should().Be("not an operation of the converter tool");
        worker.Refuse(directory, Tool(ToolContract.PdfRender, "--extra")).Should().Be("not an operation of the converter tool");
        worker.Refuse(directory, Tool()).Should().Be("not an operation of the converter tool");
    }

    [Fact]
    public void The_tool_is_capped_by_the_memory_it_writes_to_not_by_address_space()
    {
        if (!OperatingSystem.IsLinux())
        {
            SandboxedProcess.LimitCommand(SandboxLimits.Default("/tmp"), managed: true).Should().BeNull();
            return;
        }

        // A .NET process reserves gigabytes of address space it never touches
        // and does not start under --as; --data caps what it can really use.
        var limits = new SandboxLimits(TimeSpan.FromSeconds(30), 1024, "/tmp", CpuSeconds: 60);

        SandboxedProcess.LimitCommand(limits, managed: true)!.Value.Prefix.Should().Equal("--data=1024", "--cpu=60", "--");
        SandboxedProcess.LimitCommand(limits)!.Value.Prefix.Should().Equal("--as=1024", "--cpu=60", "--");
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
