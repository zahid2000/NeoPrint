using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NeoPrint.Application.Abstractions;
using NeoPrint.Converter;
using NeoPrint.Domain.Common;

namespace NeoPrint.Infrastructure.Conversion;

/// <summary>Where the converter tool is on this machine.</summary>
/// <param name="Host">The <c>dotnet</c> host that starts it.</param>
/// <param name="Assembly">The tool's DLL, next to the application's own.</param>
public sealed record ConverterToolCommand(string Host, string Assembly)
{
    /// <summary>The tool installed with this application, or null when it is not there.</summary>
    public static ConverterToolCommand? Find()
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, ToolContract.AssemblyFile);

        return File.Exists(assembly) && FindHost() is { } host ? new ConverterToolCommand(host, assembly) : null;
    }

    /// <summary>
    /// The host of the runtime this process itself runs on - the same whether
    /// the process was started by <c>dotnet</c>, by an executable of its own or
    /// by a test runner.
    /// </summary>
    private static string? FindHost()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        // <root>/shared/Microsoft.NETCore.App/<version>/ is three levels below the host.
        var beside = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", name));

        return File.Exists(beside) ? beside : SandboxedProcess.FindExecutable(name);
    }
}

/// <summary>
/// Runs the converter tool: the one way the backend opens a user's PDF or
/// image (docs/adr/ADR-0005-isolated-converter.md).
/// </summary>
/// <remarks>
/// <para>
/// PDFium, SkiaSharp and PDFsharp are not referenced by the API at all. Every
/// operation that parses a user's file - counting pages, drawing a preview,
/// turning an image into a PDF, editing pages or an image, drawing a layout -
/// is a request written into a directory of its own, carried out by a
/// short-lived process, and an answer read back. In every deployed environment
/// that process runs in the network-less converter container; on a development
/// machine without Docker it is a child process of the API.
/// </para>
/// <para>
/// The directory is in the spool - memory only - and is deleted when the
/// operation ends, whatever happened.
/// </para>
/// </remarks>
public sealed class ConverterTools : IDisposable
{
    /// <summary>
    /// The tool's memory cap. A 100-megapixel image is 400 MB decoded, and an
    /// edit holds it twice.
    /// </summary>
    public const long MemoryLimitBytes = 1280L * 1024 * 1024;

    /// <summary>How long a preview, an edit or a layout may take.</summary>
    public static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IProcessSandbox _sandbox;
    private readonly string _workRoot;
    private readonly ILogger<ConverterTools> _logger;

    /// <summary>When a refused request is told to come back.</summary>
    public const int RetryAfterSeconds = 3;

    /// <summary>
    /// One slot per tool process that may run at once for a request somebody
    /// is waiting on. A request without a slot has written nothing yet: no
    /// decrypted document waits in the spool.
    /// </summary>
    private readonly SemaphoreSlim _gate;

    private readonly int _queueLength;
    private readonly TimeSpan _queueWait;
    private int _waiting;

    public ConverterTools(IProcessSandbox sandbox, IOptions<ConversionOptions> options, ILogger<ConverterTools> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _sandbox = sandbox;
        _workRoot = options.Value.ResolveWorkRoot();
        _logger = logger;

        var parallel = Math.Max(1, options.Value.InteractiveParallel);
        _gate = new SemaphoreSlim(parallel, parallel);
        _queueLength = Math.Max(0, options.Value.InteractiveQueueLength);
        _queueWait = TimeSpan.FromSeconds(Math.Max(0, options.Value.InteractiveQueueSeconds));
    }

    /// <summary>How many requests are waiting for a slot right now.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    /// <summary>
    /// A directory for one operation, deleted when disposed. Holding one is
    /// holding a slot: at most <see cref="ConversionOptions.InteractiveParallel"/>
    /// tool processes run at once, a bounded number of requests wait behind
    /// them for a bounded time, and the rest are refused.
    /// </summary>
    /// <exception cref="FileProcessingBusyException">The queue is full, or the wait in it ran out.</exception>
    public async Task<ToolWorkspace> OpenWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            if (Interlocked.Increment(ref _waiting) > _queueLength)
            {
                Interlocked.Decrement(ref _waiting);
                throw Busy("the queue is full");
            }

            try
            {
                if (!await _gate.WaitAsync(_queueWait, cancellationToken).ConfigureAwait(false))
                {
                    throw Busy("no slot came free in time");
                }
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        try
        {
            var directory = Path.Combine(_workRoot, $"neoprint-convert-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);

            return new ToolWorkspace(directory, _gate, _logger);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private FileProcessingBusyException Busy(string reason)
    {
        _logger.LogWarning(
            "A document request was refused as busy: {Reason} ({Waiting} waiting, limit {QueueLength}, {QueueSeconds} s).",
            reason,
            Waiting,
            _queueLength,
            _queueWait.TotalSeconds);

        return new FileProcessingBusyException(RetryAfterSeconds);
    }

    /// <summary>
    /// Runs one operation in <paramref name="directory"/> and reads its answer.
    /// </summary>
    /// <param name="failFast">
    /// Someone is waiting for the answer: when the converter is not running,
    /// say so at once instead of waiting out the timeout.
    /// </param>
    /// <returns>
    /// The tool's answer. Not <see cref="ToolResponse.Ok"/> with no
    /// <see cref="ToolResponse.Error"/> means the tool itself failed - it
    /// crashed on the document or was stopped by a limit - and the caller
    /// words that for its own operation.
    /// </returns>
    /// <exception cref="FileProcessingUnavailableException">The tool could not be started at all.</exception>
    internal async Task<ToolResponse> RunAsync(
        string directory,
        string operation,
        ToolRequest request,
        TimeSpan timeout,
        bool failFast,
        CancellationToken cancellationToken)
    {
        var responsePath = Path.Combine(directory, ToolContract.ResponseFile);

        File.Delete(responsePath);

        await using (var stream = File.Create(Path.Combine(directory, ToolContract.RequestFile)))
        {
            await JsonSerializer.SerializeAsync(stream, request, Json, cancellationToken).ConfigureAwait(false);
        }

        // CPU time is summed over threads, so it is set above the wall clock.
        var limits = new SandboxLimits(timeout, MemoryLimitBytes, directory, (int)Math.Ceiling(timeout.TotalSeconds * 2))
        {
            FailFast = failFast,
        };

        var result = await _sandbox.RunAsync(ToolContract.Program, [operation], limits, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case SandboxOutcome.NotStarted:
                _logger.LogError("The converter tool did not start for {Operation}: {Reason}", operation, result.StandardError);
                throw new FileProcessingUnavailableException();

            case SandboxOutcome.TimedOut:
                return new ToolResponse(false, "conversion.timedOut");
        }

        if (!File.Exists(responsePath))
        {
            // The tool's stderr names the kind of failure only, never the document.
            _logger.LogWarning(
                "The converter tool ended {Operation} without an answer (exit {ExitCode}). Stderr: {StandardError}",
                operation,
                result.ExitCode,
                result.StandardError);

            return new ToolResponse(false);
        }

        await using var answer = File.OpenRead(responsePath);

        return await JsonSerializer.DeserializeAsync<ToolResponse>(answer, Json, cancellationToken).ConfigureAwait(false)
               ?? new ToolResponse(false);
    }

    /// <summary>What a failed operation tells the user: the tool's own words, or the caller's.</summary>
    internal static UserMessage Describe(ToolResponse response, string otherwise)
        => response.Error is { } key ? new UserMessage(key, response.Arguments) : new UserMessage(otherwise);

    /// <summary>
    /// The page count of a PDF in a conversion's own directory, or why it could
    /// not be read. This is also how a damaged or encrypted PDF is caught
    /// before it reaches a printer.
    /// </summary>
    internal async Task<(int PageCount, UserMessage? Failure)> CountPagesAsync(
        string directory, string pdfFileName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await RunAsync(
                    directory,
                    ToolContract.PdfInfo,
                    new ToolRequest(Input: pdfFileName),
                    InteractiveTimeout,
                    failFast: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return response is { Ok: true, PageCount: > 0 }
                ? (response.PageCount.Value, null)
                : (0, Describe(response, "conversion.unexpected"));
        }
        catch (FileProcessingUnavailableException)
        {
            return (0, new UserMessage("conversion.notStarted"));
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>One operation's directory in the spool.</summary>
public sealed class ToolWorkspace : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly ILogger _logger;
    private bool _disposed;

    internal ToolWorkspace(string directory, SemaphoreSlim gate, ILogger logger)
    {
        Directory = directory;
        _gate = gate;
        _logger = logger;
    }

    public string Directory { get; }

    public string PathOf(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>Writes a decrypted document into the directory.</summary>
    public async Task WriteAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        await using var file = new FileStream(
            PathOf(fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);

        await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Release();

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // In the spool, which is memory only and emptied when the stack
            // restarts; failing the request over it would be worse.
            _logger.LogWarning(exception, "A converter workspace could not be deleted.");
        }
    }
}
