using System.Text.Json;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

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

        var job = new ConverterJob(
            fileName, [.. arguments], limits.Timeout.TotalSeconds, limits.MemoryLimitBytes, limits.CpuSeconds);

        await ConverterSpool
            .WriteAtomicallyAsync(Path.Combine(directory, ConverterSpool.JobFile), job, cancellationToken)
            .ConfigureAwait(false);

        var resultPath = Path.Combine(directory, ConverterSpool.ResultFile);
        var deadline = DateTime.UtcNow + limits.Timeout + TimeSpan.FromSeconds(_options.IsolatedGraceSeconds);

        try
        {
            while (!File.Exists(resultPath))
            {
                if (DateTime.UtcNow > deadline)
                {
                    _logger.LogError(
                        "The converter container did not answer within {Seconds} s; is it running?",
                        (limits.Timeout + TimeSpan.FromSeconds(_options.IsolatedGraceSeconds)).TotalSeconds);

                    return new SandboxResult(
                        SandboxOutcome.NotStarted, null, "The isolated converter did not answer.", limits.Timeout);
                }

                await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
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
        new HashSet<string>(StringComparer.Ordinal) { "/usr/bin/soffice", "soffice", "gs", "gpcl6", "gxps" };

    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(5);

    private readonly string _root;
    private readonly IProcessSandbox _sandbox;
    private readonly IReadOnlySet<string> _programs;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _poll;

    public ConverterWorker(
        string spoolRoot,
        IProcessSandbox sandbox,
        IReadOnlySet<string> allowedPrograms,
        ILogger logger,
        int parallelJobs = 2,
        TimeSpan? poll = null)
    {
        _root = Path.GetFullPath(spoolRoot);
        _sandbox = sandbox;
        _programs = allowedPrograms;
        _logger = logger;
        _slots = new SemaphoreSlim(parallelJobs, parallelJobs);
        _poll = poll ?? TimeSpan.FromMilliseconds(200);
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
                if (_slots.CurrentCount == 0)
                {
                    break;
                }

                if (Claim(directory) is { } claimed)
                {
                    await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    running.Add(Task.Run(
                        async () =>
                        {
                            try
                            {
                                await ProcessAsync(directory, claimed, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                _slots.Release();
                            }
                        },
                        CancellationToken.None));
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
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var watch = WatchForCancelAsync(directory, cancel);

                var limits = new SandboxLimits(
                    TimeSpan.FromSeconds(job!.TimeoutSeconds), job.MemoryLimitBytes, directory, job.CpuSeconds);

                var outcome = await _sandbox.RunAsync(job.Program, job.Arguments, limits, cancel.Token)
                    .ConfigureAwait(false);

                await cancel.CancelAsync().ConfigureAwait(false);
                await watch.ConfigureAwait(false);

                result = new ConverterJobResult(
                    outcome.Outcome, outcome.ExitCode, outcome.StandardError, outcome.Duration.TotalSeconds);

                // The program and the outcome only: never the document, its
                // name or the tool's stderr, which can echo either.
                _logger.LogInformation(
                    "Conversion job {Job} with {Program}: {Outcome} in {Seconds:0.0} s.",
                    Path.GetFileName(directory),
                    Path.GetFileName(job.Program),
                    outcome.Outcome,
                    outcome.Duration.TotalSeconds);
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
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly ConversionOptions _options;

    public ConverterHealthCheck(IOptions<ConversionOptions> options) => _options = options.Value;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var heartbeat = Path.Combine(_options.ResolveWorkRoot(), ConverterSpool.HeartbeatFile);

        if (!File.Exists(heartbeat))
        {
            return Task.FromResult(HealthCheckResult.Degraded("The converter container has not started."));
        }

        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeat);

        return Task.FromResult(age <= MaxAge
            ? HealthCheckResult.Healthy($"Heartbeat {age.TotalSeconds:0} s ago.")
            : HealthCheckResult.Degraded($"No heartbeat for {age.TotalSeconds:0} s; conversions will wait."));
    }
}
