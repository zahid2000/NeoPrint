using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Infrastructure.Conversion;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The limits a conversion runs under (plan section 2.2).
/// </summary>
/// <remarks>
/// User files are untrusted input, so what matters is not that a well-formed
/// document converts - it is that a malformed one cannot hang the API, fill
/// its memory, or read its environment. These run a real child process; no
/// Docker and no converter is needed.
/// </remarks>
public sealed class SandboxedProcessTests : IDisposable
{
    private readonly SandboxedProcess _sandbox = new(NullLogger<SandboxedProcess>.Instance);
    private readonly string _workingDirectory;

    public SandboxedProcessTests()
    {
        _workingDirectory = Path.Combine(Path.GetTempPath(), $"neoprint-sandbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workingDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    private SandboxLimits Limits(TimeSpan? timeout = null) => new(
        timeout ?? TimeSpan.FromSeconds(30),
        MemoryLimitBytes: 512L * 1024 * 1024,
        _workingDirectory);

    /// <summary>A shell that exists on both platforms the backend builds on.</summary>
    private static (string FileName, string[] Arguments) Shell(string script)
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", script])
            : ("/bin/sh", ["-c", script]);

    [Fact]
    public async Task A_program_that_finishes_reports_success()
    {
        var (fileName, arguments) = Shell("exit 0");

        var result = await _sandbox.RunAsync(fileName, arguments, Limits());

        result.Succeeded.Should().BeTrue(result.StandardError);
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task A_program_that_fails_reports_its_exit_code()
    {
        var (fileName, arguments) = Shell("exit 3");

        var result = await _sandbox.RunAsync(fileName, arguments, Limits());

        result.Outcome.Should().Be(SandboxOutcome.Failed);
        result.ExitCode.Should().Be(3);
    }

    [Fact]
    public async Task A_program_that_hangs_is_killed_at_the_deadline()
    {
        // A converter given a malformed document is exactly this: busy, and
        // never finishing. Without the deadline it holds a request thread and
        // a temporary file for as long as the process lives.
        var script = OperatingSystem.IsWindows()
            ? "ping -n 60 127.0.0.1 >nul"
            : "sleep 60";

        var (fileName, arguments) = Shell(script);

        var stopwatch = Stopwatch.StartNew();
        var result = await _sandbox.RunAsync(
            fileName, arguments, Limits(TimeSpan.FromSeconds(2)));
        stopwatch.Stop();

        result.Outcome.Should().Be(SandboxOutcome.TimedOut);
        result.ExitCode.Should().BeNull();

        // Killed at the deadline, not left to finish on its own.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_child_of_the_timed_out_process_is_killed_too()
    {
        // soffice forks a child that holds the document open. Killing only the
        // parent leaves the file locked and the work still running, which is
        // why the kill covers the tree.
        const string markerName = "child-still-running.txt";
        var marker = Path.Combine(_workingDirectory, markerName);

        // Relative again, for the same quoting reason. The child inherits the
        // working directory, so it lands in the same place.
        var script = OperatingSystem.IsWindows()
            ? $"start /b cmd /c \"ping -n 30 127.0.0.1 >nul & echo alive > {markerName}\" & ping -n 30 127.0.0.1 >nul"
            : $"(sleep 20; echo alive > {markerName}) & sleep 20";

        var (fileName, arguments) = Shell(script);

        var result = await _sandbox.RunAsync(
            fileName, arguments, Limits(TimeSpan.FromSeconds(2)));

        result.Outcome.Should().Be(SandboxOutcome.TimedOut);

        // Long enough that the child would have written the marker had it
        // survived, short enough not to slow the suite down.
        await Task.Delay(TimeSpan.FromSeconds(4));

        File.Exists(marker).Should().BeFalse(
            "the child process should have been killed along with its parent");
    }

    [Fact]
    public async Task The_child_cannot_read_the_api_environment()
    {
        // The API's environment carries the OpenBao address and the paths to
        // its credentials. A converter with a parser bug must not be handed
        // them.
        Environment.SetEnvironmentVariable("NEOPRINT_SANDBOX_CANARY", "should-not-be-visible");

        try
        {
            // Written to a relative path: the child starts in its own
            // working directory, and an absolute path inside a cmd /c string
            // runs into cmd's own quoting rules.
            var script = OperatingSystem.IsWindows()
                ? "echo %NEOPRINT_SANDBOX_CANARY% > env.txt"
                : "echo \"${NEOPRINT_SANDBOX_CANARY:-unset}\" > env.txt";

            var (fileName, arguments) = Shell(script);

            var result = await _sandbox.RunAsync(fileName, arguments, Limits());

            result.Succeeded.Should().BeTrue(result.StandardError);

            var contents = await File.ReadAllTextAsync(
                Path.Combine(_workingDirectory, "env.txt"));

            contents.Should().NotContain("should-not-be-visible");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEOPRINT_SANDBOX_CANARY", null);
        }
    }

    [Fact]
    public async Task A_proxy_setting_is_not_passed_through()
    {
        // A tool that honours http_proxy would otherwise be pointed at
        // whatever the host has configured, which is the opposite of running
        // the conversion without a network.
        Environment.SetEnvironmentVariable("http_proxy", "http://proxy.invalid:3128");

        try
        {
            var script = OperatingSystem.IsWindows()
                ? "echo %http_proxy% > proxy.txt"
                : "echo \"${http_proxy:-unset}\" > proxy.txt";

            var (fileName, arguments) = Shell(script);

            await _sandbox.RunAsync(fileName, arguments, Limits());

            (await File.ReadAllTextAsync(Path.Combine(_workingDirectory, "proxy.txt")))
                .Should().NotContain("proxy.invalid");
        }
        finally
        {
            Environment.SetEnvironmentVariable("http_proxy", null);
        }
    }

    [Fact]
    public async Task The_child_starts_in_the_directory_it_was_given()
    {
        // Its own directory, deleted afterwards: a tool that writes beside the
        // input has nowhere else to put anything.
        var script = OperatingSystem.IsWindows()
            ? "cd > where.txt"
            : "pwd > where.txt";

        var (fileName, arguments) = Shell(script);

        var result = await _sandbox.RunAsync(fileName, arguments, Limits());

        result.Succeeded.Should().BeTrue(result.StandardError);
        File.Exists(Path.Combine(_workingDirectory, "where.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Standard_error_is_captured_and_bounded()
    {
        var script = OperatingSystem.IsWindows()
            ? "echo something went wrong 1>&2 & exit 1"
            : "echo 'something went wrong' >&2; exit 1";

        var (fileName, arguments) = Shell(script);

        var result = await _sandbox.RunAsync(fileName, arguments, Limits());

        result.Outcome.Should().Be(SandboxOutcome.Failed);
        result.StandardError.Should().Contain("something went wrong");

        // Bounded, so a converter in a loop cannot fill the log.
        result.StandardError.Length.Should().BeLessThan(16 * 1024);
    }

    [Fact]
    public async Task A_program_that_does_not_exist_is_reported_rather_than_thrown()
    {
        var result = await _sandbox.RunAsync(
            "neoprint-no-such-converter", [], Limits());

        result.Outcome.Should().Be(SandboxOutcome.NotStarted);

        // A missing LibreOffice is an operational problem that belongs in an
        // incident, not an unhandled exception in a request.
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_the_request_stops_the_conversion()
    {
        var script = OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 >nul" : "sleep 60";
        var (fileName, arguments) = Shell(script);

        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));

        var run = async () => await _sandbox.RunAsync(
            fileName, arguments, Limits(TimeSpan.FromMinutes(5)), cancellation.Token);

        // The caller going away has to end the work too: an abandoned upload
        // must not leave a converter running for five minutes.
        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void The_limit_wrapper_is_used_on_linux_only()
    {
        var command = SandboxedProcess.LimitCommand(Limits() with { CpuSeconds = 7 });

        if (OperatingSystem.IsLinux())
        {
            command.Should().NotBeNull();
            command!.Value.FileName.Should().Be("prlimit");

            // Before exec, so the limits apply to the converter itself rather
            // than to a shell that started it.
            command.Value.Prefix.Should().Contain("--as=536870912").And.Contain("--cpu=7");
        }
        else
        {
            // A Windows development machine runs conversions locally without
            // them; every deployed environment runs the isolated converter
            // container, whose cgroup caps CPU and memory
            // (docs/adr/ADR-0005-isolated-converter.md).
            command.Should().BeNull();
        }
    }

    [LinuxFact]
    public async Task A_program_that_spins_is_stopped_by_the_cpu_limit_long_before_its_timeout()
    {
        // One CPU second, a thirty second deadline: the kernel ends a busy
        // loop at the CPU limit, so a document that makes a converter spin
        // cannot hold a core for the whole timeout.
        var stopwatch = Stopwatch.StartNew();

        var result = await _sandbox.RunAsync(
            "/bin/sh", ["-c", "while :; do :; done"], Limits(TimeSpan.FromSeconds(30)) with { CpuSeconds = 1 });

        stopwatch.Stop();

        result.Outcome.Should().Be(SandboxOutcome.Failed, "SIGXCPU/SIGKILL ends it, not the deadline");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [LinuxFact]
    public async Task A_program_that_allocates_without_bound_is_stopped_by_the_memory_limit()
    {
        // awk grows a string until the address-space limit refuses the next
        // allocation; without the limit it would take the machine's memory.
        var stopwatch = Stopwatch.StartNew();

        var result = await _sandbox.RunAsync(
            "/usr/bin/awk",
            ["BEGIN { s = \"x\"; while (1) s = s s }"],
            new SandboxLimits(TimeSpan.FromSeconds(30), 64L * 1024 * 1024, _workingDirectory));

        stopwatch.Stop();

        result.Outcome.Should().Be(SandboxOutcome.Failed);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }
}
