using NeoPrint.Crypto.Releases;

namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// Checks a downloaded release package against the kiosk's own release keys
/// before anything of it is unpacked (plan section 7.12, step 3). The updater
/// (M17) installs a package only when this accepts it.
/// </summary>
public sealed class ReleasePackageCheck
{
    private readonly ReleaseKeySet _keys;
    private readonly ReleaseVerifier _verifier;

    /// <summary>With the key set compiled into this build.</summary>
    public ReleasePackageCheck()
        : this(ReleaseKeys.BuiltIn)
    {
    }

    /// <summary>For tests: another set than the compiled-in one.</summary>
    public ReleasePackageCheck(ReleaseKeySet keys)
    {
        if (keys.Keys.Count > 2)
        {
            throw new ArgumentException("A kiosk trusts two release keys: the current one and the next.", nameof(keys));
        }

        _keys = keys;
        _verifier = new ReleaseVerifier(keys.Keys);
    }

    public string KeySet => _keys.Name;

    /// <param name="manifestPath">The package's <c>manifest.json</c>.</param>
    /// <param name="signaturePath">Its detached signature, <c>manifest.json.sig</c>.</param>
    /// <param name="packagePath">The package file the manifest names.</param>
    /// <param name="expected">What the kiosk expects; its key set is always this build's.</param>
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
        return await CheckAsync(manifest, signature, packagePath, expected, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The same check with the manifest and signature in hand (as the backend offers them, T-321).</summary>
    public async Task<ReleaseCheck> CheckAsync(
        byte[] manifest,
        byte[] signature,
        string packagePath,
        ReleaseExpectation expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);

        await using var package = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await _verifier.CheckAsync(manifest, signature, package, expected with { KeySet = _keys.Name }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The backend's message key for a refusal (<c>updates.signatureRefused</c>).</summary>
    public static string MessageKey(ReleaseRejection rejection) => rejection switch
    {
        ReleaseRejection.UnknownKey or ReleaseRejection.BadSignature or ReleaseRejection.MalformedSignature => "updates.signatureRefused",
        ReleaseRejection.MalformedManifest => "updates.refused.malformedManifest",
        ReleaseRejection.WrongTarget => "updates.refused.wrongTarget",
        ReleaseRejection.Withdrawn => "updates.withdrawn",
        ReleaseRejection.NotNewer => "updates.notNewer",
        ReleaseRejection.AgentTooOld => "updates.agentTooOld",
        ReleaseRejection.PackageMismatch => "updates.packageMismatch",
        _ => "updates.reasonUnknown",
    };
}
