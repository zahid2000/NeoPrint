using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeoPrint.Kiosk.Core.Updates;

// ---------------------------------------------------------------------------
// How the UI and the agent talk (docs/KIOSK_UPDATES.md, T-321): files in two
// folders, each written by one side only. The UI - the backend's only client -
// downloads a package and asks for it to be installed; the agent, which holds
// no credentials, checks the package in its own folder, switches versions,
// watches the health check and writes down how it went.
// ---------------------------------------------------------------------------

/// <summary>Where the versions and the two sides' folders are on this machine.</summary>
/// <param name="InstallRoot"><c>/opt/neoprint</c>: <c>app</c>, <c>agent</c>, <c>provisioning</c>, each with <c>versions/</c> and <c>current</c>.</param>
/// <param name="UpdatesRoot"><c>/var/lib/neoprint/updates</c>: written by the UI.</param>
/// <param name="AgentRoot"><c>/var/lib/neoprint/agent</c>: written by the agent, read by the UI.</param>
public sealed record UpdatePaths(string InstallRoot, string UpdatesRoot, string AgentRoot)
{
    /// <summary>Downloaded packages: <c>&lt;releaseId&gt;.part</c> while coming, <c>&lt;releaseId&gt;.pkg</c> when complete.</summary>
    public string Downloads => Path.Combine(UpdatesRoot, "downloads");

    /// <summary>The UI's requests to the agent, one file each.</summary>
    public string Requests => Path.Combine(UpdatesRoot, "requests");

    /// <summary>The new UI's "I am healthy": <c>app-&lt;version&gt;</c>.</summary>
    public string Health => Path.Combine(UpdatesRoot, "health");

    /// <summary>The agent's answers, one file per request.</summary>
    public string Results => Path.Combine(AgentRoot, "results");

    /// <summary>The agent's own copy of a package: checked here, and only here.</summary>
    public string Incoming => Path.Combine(AgentRoot, "incoming");

    public string StateFile => Path.Combine(AgentRoot, "state.json");

    public string JournalFile => Path.Combine(AgentRoot, "journal.json");

    /// <summary>For the guard outside the agent package: "from to attempts" while a new agent proves itself.</summary>
    public string AgentPendingFile => Path.Combine(AgentRoot, "agent-pending");

    public string ComponentRoot(string component) => Path.Combine(InstallRoot, component.ToLowerInvariant());

    public string Versions(string component) => Path.Combine(ComponentRoot(component), "versions");

    public string Current(string component) => Path.Combine(ComponentRoot(component), "current");

    public string HealthMarker(string component, string version) => Path.Combine(Health, $"{component.ToLowerInvariant()}-{version}");

    /// <summary>The provisioned layout of this operating system (plan section 7.12).</summary>
    public static UpdatePaths ForThisMachine()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NeoPrint");
            return new UpdatePaths(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NeoPrint"),
                Path.Combine(data, "updates"),
                Path.Combine(data, "agent"));
        }

        return new UpdatePaths("/opt/neoprint", "/var/lib/neoprint/updates", "/var/lib/neoprint/agent");
    }

    /// <summary>The runtime identifier packages are built for on this machine.</summary>
    public static string Platform => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";
}

/// <summary>The three components a package can carry (manifest <c>component</c>).</summary>
public static class UpdateComponents
{
    public const string App = "App";
    public const string Agent = "Agent";
    public const string Provisioning = "Provisioning";

    /// <summary>The order offers are taken in: an App may need a newer agent first.</summary>
    public static readonly IReadOnlyList<string> InOrder = [Agent, Provisioning, App];
}

public enum AgentRequestKind
{
    /// <summary>Install a downloaded package.</summary>
    Install,

    /// <summary>Go back to the app version kept before the current one (<c>RollbackVersion</c>).</summary>
    Rollback,

    /// <summary>Run the current provisioning scripts again (<c>ReapplyProvisioning</c>).</summary>
    ReapplyProvisioning,

    /// <summary>Restart the agent service (<c>RestartAgent</c>).</summary>
    RestartAgent,
}

/// <summary>
/// What the UI asks of the agent. Nothing in it is taken on trust: the
/// manifest and its signature check themselves, and the package is only a
/// file name in the downloads folder.
/// </summary>
/// <param name="Id">Unique; a command's request reuses the command id, so asking twice does it once.</param>
/// <param name="PackageFile">A file name in <see cref="UpdatePaths.Downloads"/>, no folders.</param>
/// <param name="Withdrawn">SHA-256 of withdrawn packages, as the backend listed them; the agent only adds them to its own list.</param>
/// <param name="Retry">"İndi yenilə": a version that failed here before may be tried again.</param>
public sealed record AgentRequest(
    Guid Id,
    AgentRequestKind Kind,
    DateTime CreatedAt,
    Guid? CommandId = null,
    Guid? ReleaseId = null,
    string? PackageFile = null,
    string? Manifest = null,
    string? Signature = null,
    IReadOnlyList<string>? Withdrawn = null,
    int HealthTimeoutSeconds = 300,
    int KeepVersions = 3,
    bool Retry = false);

public enum AgentOutcome
{
    /// <summary>Installed (or done) and, for the app, healthy.</summary>
    Succeeded,

    /// <summary>Installed, failed its health check, taken back; or an administrator's rollback.</summary>
    RolledBack,

    /// <summary>The package did not pass the agent's check; nothing changed.</summary>
    Refused,

    /// <summary>Could not be done (disk, archive, script); nothing changed.</summary>
    Failed,
}

/// <param name="Version">The version the result is about: the one installed, refused or taken back.</param>
/// <param name="From">The version running before.</param>
/// <param name="MessageKey">Why, as a backend message key (<c>updates.healthTimeout</c>).</param>
public sealed record AgentResult(
    Guid Id,
    AgentRequestKind Kind,
    AgentOutcome Outcome,
    DateTime At,
    Guid? CommandId = null,
    string? Component = null,
    string? Version = null,
    string? From = null,
    string? MessageKey = null);

/// <summary>What the agent keeps about the machine; the UI reads it.</summary>
public sealed record AgentState
{
    /// <summary>The running agent's version, written at its start.</summary>
    public string? AgentVersion { get; init; }

    public DateTime? AgentStartedAt { get; init; }

    /// <summary>Component → the version <c>current</c> points at.</summary>
    public Dictionary<string, string> Installed { get; init; } = [];

    /// <summary>Component → the version before it, kept for a rollback.</summary>
    public Dictionary<string, string> Previous { get; init; } = [];

    /// <summary>Every SHA-256 ever named withdrawn: it only grows.</summary>
    public List<string> Withdrawn { get; init; } = [];

    /// <summary><c>Component@version</c> that failed their health check here: not installed again.</summary>
    public List<string> FailedHere { get; init; } = [];

    /// <summary>The request being carried out now, if any.</summary>
    public Guid? Busy { get; init; }
}

public enum JournalPhase
{
    /// <summary>The UI is being stopped and <c>current</c> moved; it may point either way.</summary>
    Switching,

    /// <summary><c>current</c> points at the new version; its health is being waited for.</summary>
    HealthCheck,
}

/// <summary>A switch in progress, written before each step so a power cut can be finished or undone.</summary>
public sealed record AgentJournal(
    Guid RequestId,
    AgentRequestKind Kind,
    string Component,
    string From,
    string To,
    JournalPhase Phase,
    DateTime StartedAt,
    DateTime HealthDeadline,
    Guid? CommandId = null,
    int KeepVersions = 3);

/// <summary>Reading and writing the protocol's files: atomically, and never more than a small file.</summary>
public static class AgentFiles
{
    private const long MaxFile = 256 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    /// <summary>Written next to the target, then moved over it: a reader sees the old file or the new one, never half.</summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>The file's value; null when it is missing, too large or unreadable.</summary>
    public static T? Read<T>(string path)
        where T : class
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFile)
            {
                return null;
            }

            using var stream = info.OpenRead();
            return JsonSerializer.Deserialize<T>(stream, Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
