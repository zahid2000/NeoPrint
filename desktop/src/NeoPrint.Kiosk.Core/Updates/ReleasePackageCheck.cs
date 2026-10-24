using NeoPrint.Crypto.Releases;

namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// Checks a downloaded release package against the kiosk's own release keys
/// before anything of it is unpacked (plan section 7.12, step 3). The updater
/// (M17) installs a package only when this accepts it.
/// </summary>
public sealed class ReleasePackageCheck
{
    private readonly ReleaseVerifier _verifier;

    public ReleasePackageCheck()
        : this(ReleaseKeys.Trusted)
    {
    }

    /// <summary>For tests: other keys than the compiled-in ones.</summary>
    public ReleasePackageCheck(IEnumerable<ReleasePublicKey> trusted)
    {
        _verifier = new ReleaseVerifier(trusted);
        if (_verifier.Trusted.Count > 2)
        {
            throw new ArgumentException("A kiosk trusts two release keys: the current one and the next.", nameof(trusted));
        }
    }

    /// <param name="manifestPath">The package's <c>manifest.json</c>.</param>
    /// <param name="signaturePath">Its detached signature, <c>manifest.json.sig</c>.</param>
    /// <param name="packagePath">The package file the manifest names.</param>
    public async Task<ReleaseCheck> CheckAsync(
        string manifestPath,
        string signaturePath,
        string packagePath,
        ReleaseExpectation expected,
        CancellationToken cancellationToken = default)
    {
        // Both are a few hundred bytes; a larger file is not ours.
        const int MaxSmallFile = 16 * 1024;
        if (new FileInfo(manifestPath).Length > MaxSmallFile || new FileInfo(signaturePath).Length > MaxSmallFile)
        {
            return new ReleaseCheck(ReleaseRejection.MalformedManifest, "The manifest or its signature is too large.", null);
        }

        var manifest = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var signature = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);
        await using var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await _verifier.CheckAsync(manifest, signature, package, expected, cancellationToken).ConfigureAwait(false);
    }
}
