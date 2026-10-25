using System.ComponentModel;
using System.Runtime.InteropServices;

using NeoPrint.Crypto.Releases;

namespace NeoPrint.Kiosk.Agent.Updates;

/// <summary>
/// A component's versions and its <c>current</c> link (docs/KIOSK_UPDATES.md,
/// section 2): <c>versions/&lt;v&gt;/</c> folders and <c>current</c> pointing at one.
/// </summary>
/// <remarks>
/// On Linux <c>current</c> is a symlink replaced by <c>rename(2)</c> over it -
/// atomic: whatever starts next sees the old version or the new one, never
/// none. Windows has no atomic way to replace a junction; there the old one
/// is removed and the new one made, and the agent's journal covers the
/// moment between (T-093: not checked on a real Windows kiosk yet).
/// </remarks>
public sealed class VersionLinks
{
    private readonly string _versions;
    private readonly string _current;

    public VersionLinks(string versions, string current)
    {
        _versions = versions;
        _current = current;
    }

    public string VersionFolder(string version) => Path.Combine(_versions, version);

    /// <summary>A version folder that was unpacked completely: it carries this marker last.</summary>
    public bool IsComplete(string version) => File.Exists(Path.Combine(VersionFolder(version), CompleteMarker));

    public const string CompleteMarker = ".neoprint-complete";

    /// <summary>The version <c>current</c> points at, or null when there is none (or it points at nothing).</summary>
    public string? CurrentVersion()
    {
        var info = new FileInfo(_current);
        var target = info.LinkTarget ?? new DirectoryInfo(_current).LinkTarget;
        if (target is null)
        {
            return null;
        }

        var name = Path.GetFileName(target.TrimEnd('/', '\\'));
        return Directory.Exists(VersionFolder(name)) ? name : null;
    }

    /// <summary>Points <c>current</c> at a version, atomically where the system allows it.</summary>
    public void SwitchTo(string version)
    {
        if (!Directory.Exists(VersionFolder(version)))
        {
            throw new DirectoryNotFoundException($"Version {version} is not unpacked.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_current)!);

        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(_current) || File.Exists(_current))
            {
                Directory.Delete(_current);
            }

            Directory.CreateSymbolicLink(_current, VersionFolder(version));
            return;
        }

        // Relative, so the tree can be moved or mounted elsewhere (a container, a test).
        var next = _current + ".next";
        if (new FileInfo(next).LinkTarget is not null || File.Exists(next))
        {
            File.Delete(next);
        }

        File.CreateSymbolicLink(next, Path.Combine("versions", version));

        // rename(2) itself: File.Move does not take a link to a folder for a
        // file, and anything else would leave a moment with no "current".
        if (Rename(next, _current) != 0)
        {
            throw new IOException($"current could not be pointed at {version}.", new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Rename(string from, string to);

    /// <summary>Unpacked versions, newest first.</summary>
    public IReadOnlyList<string> Installed()
        => Directory.Exists(_versions)
            ? [.. Directory.GetDirectories(_versions)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => ReleaseVersion.TryParse(name, out _))
                .OrderByDescending(name => ReleaseVersion.Parse(name))]
            : [];

    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> versions - never the
    /// ones named in <paramref name="protect"/> (current and previous) - and any
    /// half-unpacked folder.
    /// </summary>
    public void Prune(int keep, params string?[] protect)
    {
        if (!Directory.Exists(_versions))
        {
            return;
        }

        foreach (var partial in Directory.GetDirectories(_versions, "*.partial"))
        {
            Directory.Delete(partial, recursive: true);
        }

        var kept = 0;
        foreach (var version in Installed())
        {
            if (protect.Contains(version) || kept < keep)
            {
                kept++;
                continue;
            }

            Directory.Delete(VersionFolder(version), recursive: true);
        }
    }
}
