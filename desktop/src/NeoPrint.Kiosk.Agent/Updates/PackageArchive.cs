using System.Formats.Tar;
using System.IO.Compression;

namespace NeoPrint.Kiosk.Agent.Updates;

/// <summary>
/// Unpacks a release package (tar.gz on Linux, zip on Windows - the release
/// workflow's two formats) into a version folder, and nothing outside it
/// (docs/KIOSK_UPDATES.md, step 8; T-321).
/// </summary>
/// <remarks>
/// The package passed its signature check, so this is defence in depth: an
/// entry with "..", a rooted path, a link or a device is refused rather than
/// followed, and the folder is left out entirely.
/// </remarks>
public static class PackageArchive
{
    /// <summary>What an unpacked package may grow to: a kiosk build is about 150 MB.</summary>
    public const long MaxUnpacked = 2L * 1024 * 1024 * 1024;

    /// <exception cref="InvalidDataException">The archive cannot be read or holds an entry it may not.</exception>
    public static async Task ExtractAsync(string packagePath, string target, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;

        if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ExtractZip(packagePath, root, cancellationToken);
            return;
        }

        await ExtractTarGzAsync(packagePath, root, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractTarGzAsync(string packagePath, string root, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(packagePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var reader = new TarReader(gzip);
        long written = 0;

        try
        {
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
            {
                var name = entry.Name.Replace('\\', '/');
                if (name is "." or "./" || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var path = Inside(root, name);
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(path);
                        break;

                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        written += entry.Length;
                        if (written > MaxUnpacked)
                        {
                            throw new InvalidDataException("The package unpacks to more than a kiosk build can be.");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        {
                            if (entry.DataStream is { } data)
                            {
                                await data.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        if (!OperatingSystem.IsWindows())
                        {
                            // The execute bits are why Linux packages are tar: kept, nothing more than the owner's rights.
                            File.SetUnixFileMode(path, entry.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                                                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                                                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
                        }

                        break;

                    case TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes:
                        break;

                    default:
                        throw new InvalidDataException($"The package holds a {entry.EntryType} entry ({name}); only files and folders are taken.");
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or EndOfStreamException)
        {
            throw new InvalidDataException("The package archive cannot be read.", exception);
        }
    }

    private static void ExtractZip(string packagePath, string root, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        long written = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Inside(root, entry.FullName.Replace('\\', '/'));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }

            written += entry.Length;
            if (written > MaxUnpacked)
            {
                throw new InvalidDataException("The package unpacks to more than a kiosk build can be.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    /// <summary>The full path of an entry, which must be inside the version folder.</summary>
    private static string Inside(string root, string name)
    {
        if (Path.IsPathRooted(name) || name.Split('/').Any(part => part == ".."))
        {
            throw new InvalidDataException($"The package holds a path outside its folder ({name}).");
        }

        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && path + Path.DirectorySeparatorChar != root)
        {
            throw new InvalidDataException($"The package holds a path outside its folder ({name}).");
        }

        return path;
    }
}
