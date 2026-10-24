using System.Security.Cryptography;

namespace NeoPrint.Crypto.Releases;

/// <summary>Why a release package was refused.</summary>
public enum ReleaseRejection
{
    None,

    /// <summary>The signature file is unreadable.</summary>
    MalformedSignature,

    /// <summary>Signed with a key this kiosk does not trust (or no key is trusted at all).</summary>
    UnknownKey,

    /// <summary>The signature does not match the manifest: the manifest was changed, or forged.</summary>
    BadSignature,

    /// <summary>Correctly signed, but not a manifest this build can read.</summary>
    MalformedManifest,

    /// <summary>A package for another component, platform or key set.</summary>
    WrongTarget,

    /// <summary>The package was withdrawn after it was released.</summary>
    Withdrawn,

    /// <summary>Not newer than what is installed: an old package played back.</summary>
    NotNewer,

    /// <summary>The package needs a newer agent than the one installing it.</summary>
    AgentTooOld,

    /// <summary>The package file is not the one the manifest names: its size or SHA-256 differs.</summary>
    PackageMismatch,
}

/// <summary>What the kiosk expects of the package it is about to install.</summary>
/// <param name="KeySet">The key set this build carries (<see cref="ReleaseKeySet.Name"/>).</param>
/// <param name="Installed">The installed version of the component; the package must be newer. Null when nothing is installed yet.</param>
/// <param name="AgentVersion">The version of the agent doing the install.</param>
/// <param name="Withdrawn">SHA-256 (lower-case hex) of the packages withdrawn since their release.</param>
public sealed record ReleaseExpectation(
    ReleaseComponent Component,
    string Platform,
    string KeySet,
    ReleaseVersion? Installed,
    ReleaseVersion AgentVersion,
    IReadOnlyCollection<string> Withdrawn);

public sealed record ReleaseCheck(ReleaseRejection Rejection, string Detail, ReleaseManifest? Manifest)
{
    public bool Accepted => Rejection == ReleaseRejection.None;

    internal static ReleaseCheck Refused(ReleaseRejection rejection, string detail, ReleaseManifest? manifest = null) =>
        new(rejection, detail, manifest);
}

/// <summary>
/// Decides whether a downloaded release package may be installed
/// (plan section 7.12, docs/RELEASE_SIGNING.md).
/// </summary>
/// <remarks>
/// The order matters: nothing of the manifest is believed before its signature
/// checks out against one of the trusted keys, and the package bytes are hashed
/// only once the manifest says what they must be.
/// </remarks>
public sealed class ReleaseVerifier
{
    private readonly IReadOnlyList<ReleasePublicKey> _trusted;

    public ReleaseVerifier(IEnumerable<ReleasePublicKey> trusted)
    {
        _trusted = trusted.ToList();
        if (_trusted.Select(k => k.KeyId).Distinct(StringComparer.Ordinal).Count() != _trusted.Count)
        {
            throw new ArgumentException("A trusted release key is listed twice.", nameof(trusted));
        }
    }

    public IReadOnlyList<ReleasePublicKey> Trusted => _trusted;

    public async Task<ReleaseCheck> CheckAsync(
        byte[] manifestBytes,
        byte[] signatureBytes,
        Stream package,
        ReleaseExpectation expected,
        CancellationToken cancellationToken = default)
    {
        var manifestCheck = CheckManifest(manifestBytes, signatureBytes, expected);
        if (!manifestCheck.Accepted)
        {
            return manifestCheck;
        }

        var manifest = manifestCheck.Manifest!;
        if (package.CanSeek && package.Length != manifest.Size)
        {
            return ReleaseCheck.Refused(ReleaseRejection.PackageMismatch,
                $"The package is {package.Length} bytes; the manifest says {manifest.Size}.", manifest);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await package.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            size += read;
            if (size > manifest.Size)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return size == manifest.Size && actual == manifest.Sha256
            ? manifestCheck
            : ReleaseCheck.Refused(ReleaseRejection.PackageMismatch,
                $"The package's SHA-256 is not the one in the manifest ({manifest.Sha256}).", manifest);
    }

    /// <summary>Everything but the package bytes: the signature, then what the manifest says.</summary>
    public ReleaseCheck CheckManifest(byte[] manifestBytes, byte[] signatureBytes, ReleaseExpectation expected)
    {
        ReleaseSignature signature;
        try
        {
            signature = ReleaseSignature.Parse(signatureBytes);
        }
        catch (FormatException e)
        {
            return ReleaseCheck.Refused(ReleaseRejection.MalformedSignature, e.Message);
        }

        var key = _trusted.FirstOrDefault(k => string.Equals(k.KeyId, signature.KeyId, StringComparison.Ordinal));
        if (key is null)
        {
            return ReleaseCheck.Refused(ReleaseRejection.UnknownKey, _trusted.Count == 0
                ? "This build trusts no release key."
                : $"Signed with key {signature.KeyId}; this build trusts {string.Join(", ", _trusted.Select(k => k.KeyId))}.");
        }

        if (!signature.IsValidFor(manifestBytes, key))
        {
            return ReleaseCheck.Refused(ReleaseRejection.BadSignature, $"The signature of key {key.KeyId} does not match the manifest.");
        }

        ReleaseManifest manifest;
        try
        {
            manifest = ReleaseManifest.Parse(manifestBytes);
        }
        catch (FormatException e)
        {
            return ReleaseCheck.Refused(ReleaseRejection.MalformedManifest, e.Message);
        }

        if (manifest.Component != expected.Component
            || !string.Equals(manifest.Platform, expected.Platform, StringComparison.Ordinal)
            || !string.Equals(manifest.KeySet, expected.KeySet, StringComparison.Ordinal))
        {
            return ReleaseCheck.Refused(ReleaseRejection.WrongTarget,
                $"The package is {manifest.Component} for {manifest.Platform} ({manifest.KeySet}); expected {expected.Component} for {expected.Platform} ({expected.KeySet}).", manifest);
        }

        if (expected.Withdrawn.Contains(manifest.Sha256, StringComparer.Ordinal))
        {
            return ReleaseCheck.Refused(ReleaseRejection.Withdrawn, $"{manifest.Component} {manifest.Version} was withdrawn.", manifest);
        }

        if (expected.Installed is { } installed && manifest.Version <= installed)
        {
            return ReleaseCheck.Refused(ReleaseRejection.NotNewer,
                $"{manifest.Component} {manifest.Version} is not newer than the installed {installed}.", manifest);
        }

        if (expected.AgentVersion < manifest.MinAgentVersion)
        {
            return ReleaseCheck.Refused(ReleaseRejection.AgentTooOld,
                $"{manifest.Component} {manifest.Version} needs agent {manifest.MinAgentVersion}; this is {expected.AgentVersion}.", manifest);
        }

        return new ReleaseCheck(ReleaseRejection.None, $"{manifest.Component} {manifest.Version} for {manifest.Platform}, key {key.KeyId}.", manifest);
    }
}
