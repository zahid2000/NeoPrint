using NeoPrint.Crypto;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>
/// The kiosk's encrypted spool: prepared print files waiting for the printer
/// (plan sections 7.4, 7.5; docs/SECURITY_THREAT_MODEL.md).
/// </summary>
/// <remarks>
/// <para>
/// Everything at rest is AES-256-GCM in the same chunked envelope format the
/// backend's storage uses (<c>NeoPrint.Crypto</c>), under a key that never
/// leaves the machine unprotected (<see cref="ISpoolKeyStore"/>). A flash
/// drive pulled out after the order still prints, because the file was
/// copied in here when the order was placed (plan section 7.5).
/// </para>
/// <para>
/// A file is decrypted only into a private working folder for the moment the
/// printer needs it, and deleted straight after; the working folder is
/// emptied at every start, so a crash leaves nothing readable behind.
/// </para>
/// </remarks>
public sealed class SpoolStore
{
    private const string KeyId = "spool";

    private readonly IAppPaths _paths;
    private readonly ISpoolKeyStore _keys;

    public SpoolStore(IAppPaths paths, ISpoolKeyStore keys)
    {
        _paths = paths;
        _keys = keys;
    }

    private string JobsFolder => Path.Combine(_paths.SpoolDirectory, "jobs");

    /// <summary>Where plaintext lives while it is being worked on.</summary>
    public string WorkFolder => Path.Combine(_paths.SpoolDirectory, "work");

    /// <summary>Encrypts <paramref name="plaintext"/> into the spool under <paramref name="name"/>.</summary>
    public async Task PutAsync(string name, Stream plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        Directory.CreateDirectory(JobsFolder);

        var target = PathOf(name);
        var temporary = target + ".partial";

        // Written aside and moved into place: a crash mid-write leaves a
        // .partial that is never mistaken for a complete file.
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await Encryptor().EncryptAsync(plaintext, output, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, target, overwrite: true);
    }

    public bool Exists(string name) => File.Exists(PathOf(name));

    /// <summary>Decrypts a spooled file into a fresh private working folder.</summary>
    /// <returns>The plaintext copy; delete its folder with <see cref="Discard"/>.</returns>
    public async Task<string> OpenAsync(string name, CancellationToken cancellationToken = default)
    {
        var folder = NewWorkFolder();
        var target = Path.Combine(folder, name);

        await using (var input = new FileStream(PathOf(name), FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await Encryptor().DecryptAsync(input, output, cancellationToken).ConfigureAwait(false);
        }

        return target;
    }

    /// <summary>A new empty folder under the working area, for one document's processing.</summary>
    public string NewWorkFolder()
    {
        var folder = Path.Combine(WorkFolder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        return folder;
    }

    /// <summary>Deletes a working copy and its folder.</summary>
    public void Discard(string? pathInWorkFolder)
    {
        if (string.IsNullOrEmpty(pathInWorkFolder))
        {
            return;
        }

        var folder = Directory.Exists(pathInWorkFolder) ? pathInWorkFolder : Path.GetDirectoryName(pathInWorkFolder);

        if (folder is not null
            && Path.GetFullPath(folder).StartsWith(Path.GetFullPath(WorkFolder), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    public void Delete(string name)
    {
        var path = PathOf(name);

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Empties the working area: at start, after a crash.</summary>
    public void ClearWork()
    {
        if (Directory.Exists(WorkFolder))
        {
            Directory.Delete(WorkFolder, recursive: true);
        }
    }

    /// <summary>
    /// Everything, key included: the device was detached, and what it held
    /// must not be readable by whoever gets the machine next (plan 7.11).
    /// </summary>
    public void Wipe()
    {
        ClearWork();

        if (Directory.Exists(JobsFolder))
        {
            Directory.Delete(JobsFolder, recursive: true);
        }

        _keys.Delete();
    }

    private string PathOf(string name)
    {
        // Names are ours (job-{id}.pdf), but a name with a path in it must
        // never reach outside the spool.
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name))
        {
            throw new ArgumentException("A spool name is a plain file name.", nameof(name));
        }

        return Path.Combine(JobsFolder, name + ".npenc");
    }

    private EnvelopeEncryptor Encryptor()
        => new(new KeyRing([new KeyEncryptionKey(KeyId, _keys.GetOrCreateKey())], KeyId));
}
