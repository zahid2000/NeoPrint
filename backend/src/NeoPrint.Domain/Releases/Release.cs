using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Releases;

/// <summary>What a release package carries (plan section 5, <c>releases.Component</c>).</summary>
public enum ReleaseComponent
{
    App = 0,
    Agent = 1,
    Provisioning = 2,
}

/// <summary>
/// How far a release has come (plan section 7.12, step 2): first the pilot
/// kiosks, then everyone.
/// </summary>
public enum ReleaseChannel
{
    Pilot = 0,
    Stable = 1,
}

/// <summary>Where the stored package lives (the same fields as a stored file).</summary>
public sealed class ReleasePackage
{
    public Guid ProviderId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>SHA-256 of the encrypted bytes in the store.</summary>
    public string StoredSha256 { get; set; } = string.Empty;

    public long EncryptedLength { get; set; }

    public string KekId { get; set; } = string.Empty;
}

/// <summary>
/// One signed kiosk package: the UI, the agent or the provisioning scripts for
/// one platform (plan section 5, <c>releases</c>; docs/RELEASE_SIGNING.md).
/// </summary>
/// <remarks>
/// <para>
/// Everything about the package comes from its signed manifest, which is kept
/// byte for byte with its signature: a kiosk checks the very bytes the owner
/// signed, never something the backend rebuilt (T-313).
/// </para>
/// <para>
/// A release is never deleted while kiosks may still run it; it is withdrawn,
/// and the kiosks are told which packages not to install any more.
/// </para>
/// </remarks>
public sealed class Release : Entity
{
    public ReleaseComponent Component { get; set; }

    /// <summary><c>win-x64</c> or <c>linux-x64</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>The release key set the package was built and signed for (T-316).</summary>
    public string KeySet { get; set; } = string.Empty;

    /// <summary>SemVer, as in the manifest.</summary>
    public string Version { get; set; } = string.Empty;

    public string MinAgentVersion { get; set; } = string.Empty;

    /// <summary>The package's file name, as in the manifest.</summary>
    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>SHA-256 of the package itself (lower-case hex), as in the manifest.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public string? Commit { get; set; }

    public DateTime BuiltAt { get; set; }

    /// <summary>The release key that signed it.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The manifest exactly as signed (UTF-8 JSON).</summary>
    public string Manifest { get; set; } = string.Empty;

    /// <summary>The detached signature file exactly as uploaded.</summary>
    public string Signature { get; set; } = string.Empty;

    public ReleasePackage Package { get; set; } = new();

    public ReleaseChannel Channel { get; private set; } = ReleaseChannel.Pilot;

    public Guid? UploadedBy { get; set; }

    public bool IsWithdrawn { get; private set; }

    public DateTime? WithdrawnAt { get; private set; }

    public Guid? WithdrawnBy { get; private set; }

    /// <summary>Why it was withdrawn, in the administrator's words.</summary>
    public string? WithdrawReason { get; private set; }

    /// <summary>The release is put on a channel when it is uploaded.</summary>
    public void PublishTo(ReleaseChannel channel) => Channel = channel;

    /// <summary>From the pilot kiosks to everyone. Promoting twice changes nothing.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Promote()
    {
        if (IsWithdrawn)
        {
            throw new DomainException("releases.withdrawn");
        }

        if (Channel == ReleaseChannel.Stable)
        {
            return false;
        }

        Channel = ReleaseChannel.Stable;
        return true;
    }

    /// <summary>No kiosk installs it any more. Withdrawing twice changes nothing.</summary>
    /// <returns>Whether anything changed.</returns>
    public bool Withdraw(Guid adminUserId, string reason, DateTime now)
    {
        if (IsWithdrawn)
        {
            return false;
        }

        IsWithdrawn = true;
        WithdrawnAt = now;
        WithdrawnBy = adminUserId;
        WithdrawReason = reason;
        return true;
    }
}
