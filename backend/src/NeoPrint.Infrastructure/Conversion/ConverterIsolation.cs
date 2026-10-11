using System.Text.Json;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NeoPrint.Converter;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>Runs a converter program under limits; where it runs is the implementation's business.</summary>
public interface IProcessSandbox
{
    Task<SandboxResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        SandboxLimits limits,
        CancellationToken cancellationToken = default);
}

/// <summary>Where conversions run (<c>Conversion:*</c>, docs/adr/ADR-0005-isolated-converter.md).</summary>
public sealed class ConversionOptions
{
    public const string SectionName = "Conversion";

    /// <summary>A child process of the API: development machines without Docker.</summary>
    public const string LocalMode = "Local";

    /// <summary>The network-less converter container, through the spool: every deployed environment.</summary>
    public const string IsolatedMode = "Isolated";

    public string Mode { get; set; } = LocalMode;

    /// <summary>
    /// Where the per-conversion directories are made. In Isolated mode this is
    /// the spool the API and the converter container share (a tmpfs volume,
    /// so no document is written to disk); otherwise the system temp folder.
    /// </summary>
    public string? WorkRoot { get; set; }

    /// <summary>How long past a conversion's own timeout the API waits for the converter to answer.</summary>
    public int IsolatedGraceSeconds { get; set; } = 30;

    public bool IsIsolated => string.Equals(Mode, IsolatedMode, StringComparison.OrdinalIgnoreCase);

    public string ResolveWorkRoot() => string.IsNullOrWhiteSpace(WorkRoot) ? Path.GetTempPath() : WorkRoot;

    /// <summary>
    /// Outside Development, conversions must be isolated: a local child process
    /// shares the API's network and user, which is exactly what the untrusted
    /// file rule (CLAUDE.md) forbids.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration would run converters inside the API.</exception>
    public void EnsureAllowedIn(bool isDevelopment)
    {
        if (!isDevelopment && !IsIsolated)
        {
            throw new InvalidOperationException(
                $"Conversion:Mode is '{Mode}'. Outside Development conversions must run in the isolated "
                + "converter container (Conversion:Mode=Isolated, docs/adr/ADR-0005-isolated-converter.md).");
        }

        if (IsIsolated && string.IsNullOrWhiteSpace(WorkRoot))
        {
            throw new InvalidOperationException(
                "Conversion:Mode is Isolated but Conversion:WorkRoot is not set: the API and the converter "
                + "container need a shared spool directory.");
        }
    }
}

/// <summary>A conversion request, written into its own directory in the spool.</summary>
/// <param name="Program">The converter program. The worker only runs programs on its allowlist.</param>
public sealed record ConverterJob(
    string Program,
    IReadOnlyList<string> Arguments,
    double TimeoutSeconds,
    long MemoryLimitBytes,
    int CpuSeconds);

/// <summary>What the worker writes back.</summary>
public sealed record ConverterJobResult(
    SandboxOutcome Outcome,
    int? ExitCode,
    string StandardError,
    double DurationSeconds);

/// <summary>The file names of the spool protocol, shared by both sides.</summary>
public static class ConverterSpool
{
    public const string JobFile = "job.json";
    public const string ClaimedFile = "job.claimed";
    public const string ResultFile = "result.json";
    public const string CancelFile = "cancel";

    /// <summary>Touched by the worker every few seconds; the API's health check reads its age.</summary>
    public const string HeartbeatFile = ".converter-alive";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The worker touches its heartbeat every five seconds; this long without
    /// one and the converter counts as down - for the health check, and for a
    /// request someone is waiting on, which is then told so instead of queued.
    /// One value for both, so health never says "healthy" about a converter
    /// that requests are already being refused for.
    /// </summary>
    public static readonly TimeSpan DownAfter = TimeSpan.FromSeconds(20);

    /// <summary>Whether the worker has stopped touching its heartbeat.</summary>
    public static bool IsDown(string spoolRoot) => HeartbeatAge(spoolRoot) is not { } age || age > DownAfter;

    /// <summary>How long ago the worker last touched its heartbeat, or null when it never has.</summary>
    public static TimeSpan? HeartbeatAge(string spoolRoot)
    {
        var heartbeat = Path.Combine(spoolRoot, HeartbeatFile);

        // A missing file has the year 1601 as its time rather than an error.
        return File.Exists(heartbeat) ? DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeat) : null;
    }

    /// <summary>Writes a file so that a reader sees all of it or none of it.</summary>
    public static async Task WriteAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";

        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Hands a conversion to the isolated converter container and waits for it
/// (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// The API and the container share nothing but the spool: a tmpfs volume in
/// which each conversion has its own directory. The API writes the input and
/// <c>job.json</c> there; the container - no network, read-only root, no
/// secrets, CPU and memory capped by its cgroup - runs the program and writes
/// <c>result.json</c> and the PDF next to them.
/// </remarks>
public sealed class IsolatedProcessSandbox : IProcessSandbox
{
    private static readonly TimeSpan FirstPoll = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan SlowestPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>How long past its timeout a request someone is waiting on is given.</summary>
    private static readonly TimeSpan FailFastGrace = TimeSpan.FromSeconds(5);

    private readonly ConversionOptions _options;
    private readonly ILogger<IsolatedProcessSandbox> _logger;

    public IsolatedProcessSandbox(IOptions<ConversionOptions> options, ILogger<IsolatedProcessSandbox> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SandboxResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        SandboxLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(limits);

        var directory = Path.GetFullPath(limits.WorkingDirectory);
        var root = Path.GetFullPath(_options.ResolveWorkRoot());

        // The container sees only the spool; a directory anywhere else would
        // not exist on its side.
        if (!string.Equals(
                Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The conversion directory must be a direct child of the spool {root}.");
        }

        if (limits.FailFast && ConverterSpool.IsDown(root))
        {
            return new SandboxResult(
                SandboxOutcome.NotStarted, null, "The isolated converter is not running.", TimeSpan.Zero);
        }

        // A directory can carry more than one job, one after another - a
        // conversion and then the page count of its result. What the previous
        // one left would be read as this one's answer.
        foreach (var leftover in new[] { ConverterSpool.ResultFile, ConverterSpool.ClaimedFile, ConverterSpool.CancelFile })
        {
            File.Delete(Path.Combine(directory, leftover));
        }

        var job = new ConverterJob(
            fileName, [.. arguments], limits.Timeout.TotalSeconds, limits.MemoryLimitBytes, limits.CpuSeconds);

        await ConverterSpool
            .WriteAtomicallyAsync(Path.Combine(directory, ConverterSpool.JobFile), job, cancellationToken)
            .ConfigureAwait(false);

        var resultPath = Path.Combine(directory, ConverterSpool.ResultFile);
        var patience = limits.Timeout
                       + (limits.FailFast ? FailFastGrace : TimeSpan.FromSeconds(_options.IsolatedGraceSeconds));
        var deadline = DateTime.UtcNow + patience;
        var poll = FirstPoll;
        var nextLifeCheck = DateTime.UtcNow + TimeSpan.FromSeconds(1);

        try
        {
            while (!File.Exists(resultPath))
            {
                var now = DateTime.UtcNow;

                if (now > deadline)
                {
                    _logger.LogError(
                        "The converter container did not answer within {Seconds} s; is it running?", patience.TotalSeconds);

                    return new SandboxResult(
                        SandboxOutcome.NotStarted, null, "The isolated converter did not answer.", limits.Timeout);
                }

                if (limits.FailFast && now > nextLifeCheck)
                {
                    if (ConverterSpool.IsDown(root))
                    {
                        _logger.LogError("The converter container stopped while a request was waiting on it.");

                        return new SandboxResult(
                            SandboxOutcome.NotStarted, null, "The isolated converter stopped.", limits.Timeout);
                    }

                    nextLifeCheck = now + TimeSpan.FromSeconds(1);
                }

                // Short operations are answered in tens of milliseconds; long
                // ones are not worth looking at that often.
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
                poll = poll < SlowestPoll ? poll * 2 : SlowestPoll;
            }
        }
        catch (OperationCanceledException)
        {
            // Tell the worker to stop the program; the directory is deleted by
            // the caller either way.
            await File.WriteAllTextAsync(Path.Combine(directory, ConverterSpool.CancelFile), string.Empty, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        var result = await ConverterSpool.ReadAsync<ConverterJobResult>(resultPath, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The converter wrote an empty result.");

        return new SandboxResult(
            result.Outcome, result.ExitCode, result.StandardError, TimeSpan.FromSeconds(result.DurationSeconds));
    }
}

/// <summary>
/// The loop inside the converter container: claims jobs from the spool, runs
/// them, writes the results (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// It trusts nothing in <c>job.json</c> beyond what it checks: the program must
/// be on the allowlist, and the only directory it works in is the job's own.
/// The limits are applied again here, by <see cref="SandboxedProcess"/>
/// (<c>prlimit</c>), on top of the container's cgroup.
/// </remarks>
public sealed class ConverterWorker
{
    /// <summary>The command-line switch that starts the API binary as the converter.</summary>
    public const string CommandLineSwitch = "--converter-worker";

    /// <summary>The converter programs the image carries (backend/Dockerfile).</summary>
    public static readonly IReadOnlySet<string> DefaultPrograms =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "/usr/bin/soffice", "soffice", "gs", "gpcl6", "gxps", ToolContract.Program,
        };

    /// <summary>What the converter tool can be asked to do; anything else is refused.</summary>
    private static readonly HashSet<string> ToolOperations = new(StringComparer.Ordinal)
    {
        ToolContract.PdfInfo,
        ToolContract.PdfRender,
        ToolContract.ImageToPdf,
        ToolContract.EditPages,
        ToolContract.EditImage,
        ToolContract.LayoutRender,
    };

    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

    private readonly string _root;
    private readonly IProcessSandbox _sandbox;
    private readonly IReadOnlySet<string> _programs;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _toolSlots;
    private readonly TimeSpan _poll;

    public ConverterWorker(
        string spoolRoot,
        IProcessSandbox sandbox,
        IReadOnlySet<string> allowedPrograms,
        ILogger logger,
        int parallelJobs = 2,
        TimeSpan? poll = null,
        int parallelTools = 3)
    {
        _root = Path.GetFullPath(spoolRoot);
        _sandbox = sandbox;
        _programs = allowedPrograms;
        _logger = logger;
        _slots = new SemaphoreSlim(parallelJobs, parallelJobs);

        // A preview or a page count takes a fraction of a second and must not
        // queue behind two office documents that each take a minute.
        _toolSlots = new SemaphoreSlim(parallelTools, parallelTools);
        _poll = poll ?? TimeSpan.FromMilliseconds(50);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Converter worker watching {Spool}.", _root);

        var running = new List<Task>();
        var lastHeartbeat = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (DateTime.UtcNow - lastHeartbeat > HeartbeatEvery)
            {
                await File.WriteAllTextAsync(
                        Path.Combine(_root, ConverterSpool.HeartbeatFile), DateTime.UtcNow.ToString("O"), cancellationToken)
                    .ConfigureAwait(false);
                lastHeartbeat = DateTime.UtcNow;
            }

            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                // Claimed at once; the job then waits inside ProcessAsync for
                // a slot of its own kind.
                if (Claim(directory) is { } claimed)
                {
                    running.Add(Task.Run(
                        () => ProcessAsync(directory, claimed, cancellationToken), CancellationToken.None));
                }
            }

            running.RemoveAll(task => task.IsCompleted);

            try
            {
                await Task.Delay(_poll, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Takes a job so no other worker does: renaming is atomic.</summary>
    internal static string? Claim(string directory)
    {
        var job = Path.Combine(directory, ConverterSpool.JobFile);
        var claimed = Path.Combine(directory, ConverterSpool.ClaimedFile);

        if (!File.Exists(job))
        {
            return null;
        }

        try
        {
            File.Move(job, claimed);
            return claimed;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Runs one claimed job and writes its result.</summary>
    internal async Task ProcessAsync(string directory, string claimedPath, CancellationToken cancellationToken)
    {
        var resultPath = Path.Combine(directory, ConverterSpool.ResultFile);
        ConverterJobResult result;

        try
        {
            var job = await ConverterSpool.ReadAsync<ConverterJob>(claimedPath, cancellationToken).ConfigureAwait(false);

            if (Refuse(directory, job) is { } reason)
            {
                _logger.LogWarning("Refused a conversion job in {Directory}: {Reason}", Path.GetFileName(directory), reason);
                result = new ConverterJobResult(SandboxOutcome.NotStarted, null, "Refused: " + reason, 0);
            }
            else
            {
                var slots = string.Equals(job!.Program, ToolContract.Program, StringComparison.Ordinal) ? _toolSlots : _slots;

                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    result = await RunJobAsync(directory, job, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            _logger.LogWarning(exception, "Could not read the conversion job in {Directory}.", Path.GetFileName(directory));
            result = new ConverterJobResult(SandboxOutcome.NotStarted, null, "Refused: unreadable job.", 0);
        }
        catch (OperationCanceledException)
        {
            result = new ConverterJobResult(SandboxOutcome.Failed, null, "Cancelled.", 0);
        }

        if (Directory.Exists(directory))
        {
            await ConverterSpool.WriteAtomicallyAsync(resultPath, result, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<ConverterJobResult> RunJobAsync(string directory, ConverterJob job, CancellationToken cancellationToken)
    {
        // The caller gave up while the job waited for its slot.
        if (!Directory.Exists(directory) || File.Exists(Path.Combine(directory, ConverterSpool.CancelFile)))
        {
            return new ConverterJobResult(SandboxOutcome.Failed, null, "Cancelled.", 0);
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = WatchForCancelAsync(directory, cancel);

        var limits = new SandboxLimits(
            TimeSpan.FromSeconds(job.TimeoutSeconds), job.MemoryLimitBytes, directory, job.CpuSeconds);

        var outcome = await _sandbox.RunAsync(job.Program, job.Arguments, limits, cancel.Token).ConfigureAwait(false);

        await cancel.CancelAsync().ConfigureAwait(false);
        await watch.ConfigureAwait(false);

        // The program and the outcome only: never the document, its name or
        // the tool's stderr, which can echo either.
        _logger.LogInformation(
            "Conversion job {Job} with {Program}: {Outcome} in {Seconds:0.0} s.",
            Path.GetFileName(directory),
            job.Program == ToolContract.Program ? job.Arguments[0] : Path.GetFileName(job.Program),
            outcome.Outcome,
            outcome.Duration.TotalSeconds);

        return new ConverterJobResult(outcome.Outcome, outcome.ExitCode, outcome.StandardError, outcome.Duration.TotalSeconds);
    }

    /// <summary>Why a job is not run, or null when it may be.</summary>
    public string? Refuse(string directory, ConverterJob? job)
    {
        if (job is null)
        {
            return "empty job";
        }

        if (!_programs.Contains(job.Program))
        {
            return $"'{job.Program}' is not a converter this container runs";
        }

        // The tool is ours, but the job file is not trusted any more for it:
        // one known operation, and nothing else on its command line.
        if (string.Equals(job.Program, ToolContract.Program, StringComparison.Ordinal)
            && (job.Arguments is not [var operation] || !ToolOperations.Contains(operation)))
        {
            return "not an operation of the converter tool";
        }

        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), _root, StringComparison.Ordinal))
        {
            return "the job is not in the spool";
        }

        if (job.TimeoutSeconds is <= 0 or > 600 || job.MemoryLimitBytes <= 0 || job.CpuSeconds is <= 0 or > 1200)
        {
            return "limits out of range";
        }

        return null;
    }

    private static async Task WatchForCancelAsync(string directory, CancellationTokenSource cancel)
    {
        var marker = Path.Combine(directory, ConverterSpool.CancelFile);

        try
        {
            while (!cancel.IsCancellationRequested)
            {
                if (File.Exists(marker) || !Directory.Exists(directory))
                {
                    await cancel.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await Task.Delay(200, cancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Finished normally.
        }
    }
}

/// <summary>Whether the converter container is alive, from the age of its heartbeat file.</summary>
public sealed class ConverterHealthCheck : IHealthCheck
{
    private readonly ConversionOptions _options;

    public ConverterHealthCheck(IOptions<ConversionOptions> options) => _options = options.Value;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (ConverterSpool.HeartbeatAge(_options.ResolveWorkRoot()) is not { } age)
        {
            return Task.FromResult(HealthCheckResult.Degraded("The converter container has not started."));
        }

        return Task.FromResult(age <= ConverterSpool.DownAfter
            ? HealthCheckResult.Healthy($"Heartbeat {age.TotalSeconds:0} s ago.")
            : HealthCheckResult.Degraded($"No heartbeat for {age.TotalSeconds:0} s; conversions will wait."));
    }
}
