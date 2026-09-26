using System.Security.Cryptography;

using NeoPrint.Domain.Common;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Devices;

/// <summary>
/// Lifecycle of a kiosk device (plan section 7.11).
/// </summary>
public enum DeviceStatus
{
    /// <summary>Registered, showing a pairing code, waiting for an admin.</summary>
    Pending = 0,

    /// <summary>Activated and bound to a kiosk.</summary>
    Active = 1,

    /// <summary>Detached by an admin; its key is no longer accepted.</summary>
    Revoked = 2,

    /// <summary>Never activated within the pending window.</summary>
    Expired = 3,
}

/// <summary>
/// What the device reports about the machine it runs on (plan section 7.11).
/// </summary>
/// <remarks>
/// Shown to the admin who activates the device, so they can tell which physical
/// machine they are looking at. A substantial change later - a different
/// machine id, say - raises a medium incident.
/// </remarks>
public sealed class DeviceFingerprint
{
    public string? Hostname { get; set; }

    public string? Os { get; set; }

    public string? OsVersion { get; set; }

    public string? MachineId { get; set; }

    public List<string> MacAddresses { get; set; } = [];

    public string? Cpu { get; set; }

    public int RamGb { get; set; }

    public string? ScreenResolution { get; set; }

    /// <summary>
    /// Whether this looks like the same machine. The machine id is decisive
    /// when both sides have one; otherwise the hostname and MAC addresses are
    /// compared, which is enough to notice hardware being swapped underneath.
    /// </summary>
    public bool LooksLikeSameMachine(DeviceFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.IsNullOrWhiteSpace(MachineId) && !string.IsNullOrWhiteSpace(other.MachineId))
        {
            return string.Equals(MachineId, other.MachineId, StringComparison.OrdinalIgnoreCase);
        }

        var sameHost = string.Equals(Hostname, other.Hostname, StringComparison.OrdinalIgnoreCase);
        var sharesMac = MacAddresses.Intersect(other.MacAddresses, StringComparer.OrdinalIgnoreCase).Any();

        return sameHost && sharesMac;
    }
}

/// <summary>
/// A kiosk device: one installation of the universal kiosk build, identified by
/// a key pair it generated itself (plan section 7.11).
/// </summary>
/// <remarks>
/// The private key never leaves the machine - it lives in the TPM where there
/// is one. The backend only ever holds the public key, so nothing it stores can
/// impersonate a kiosk.
/// </remarks>
public sealed class KioskDevice : Entity
{
    /// <summary>How long a pairing code stays valid.</summary>
    public static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(30);

    /// <summary>A device left pending longer than this expires.</summary>
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);

    /// <summary>ECDSA P-256 public key in SPKI form, base64.</summary>
    public string PublicKey { get; set; } = string.Empty;

    public DeviceFingerprint Fingerprint { get; set; } = new();

    public KioskPlatform Platform { get; set; } = KioskPlatform.Unknown;

    public string? AppVersion { get; set; }

    /// <summary>
    /// Where the device is in its lifecycle. Changed only through the
    /// transition methods below, never assigned from outside
    /// (<c>docs/STATE_MACHINES.md</c>).
    /// </summary>
    public DeviceStatus Status { get; private set; } = DeviceStatus.Pending;

    /// <summary>Hashed: the screen shows the code, the database never stores it.</summary>
    public string? PairingCodeHash { get; set; }

    public DateTime? PairingCodeExpiresAt { get; set; }

    public Guid? KioskId { get; set; }

    /// <summary>
    /// The kiosk the device was bound to before it was revoked, for the
    /// kiosk's device history (plan section 8A.3). <see cref="KioskId"/> is
    /// cleared on revocation, because a revoked device acts for nobody.
    /// </summary>
    public Guid? LastKioskId { get; set; }

    public Guid? ActivatedBy { get; set; }

    public DateTime? ActivatedAt { get; set; }

    public Guid? RevokedBy { get; set; }

    public DateTime? RevokedAt { get; set; }

    public string? RevokeReason { get; set; }

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public string? LastIp { get; set; }

    /// <summary>Printers reported before activation, so the admin can pick one straight away.</summary>
    public List<AvailablePrinter> ReportedPrinters { get; set; } = [];

    public string? AgentVersion { get; set; }

    public string? ProvisioningVersion { get; set; }

    /// <summary>The enrollment token that activated this device, when one was used.</summary>
    public Guid? EnrollmentTokenId { get; set; }

    public bool IsPairingCodeValid(DateTime utcNow)
        => PairingCodeHash is not null && PairingCodeExpiresAt > utcNow;

    /// <summary>
    /// Generates a pairing code of the form <c>XXXX-XXXX</c> from the
    /// unambiguous alphabet, so it can be read off a kiosk screen over the
    /// phone without confusion (plan section 7.11).
    /// </summary>
    public static string NewPairingCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

        return string.Create(9, alphabet, (span, letters) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = i == 4 ? '-' : letters[RandomNumberGenerator.GetInt32(letters.Length)];
            }
        });
    }

    /// <summary>Codes are compared without the separator and case-insensitively.</summary>
    public static string NormalizePairingCode(string code)
        => code.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();

    /// <summary>
    /// The transitions a device may make (<c>docs/STATE_MACHINES.md</c>).
    /// </summary>
    /// <remarks>
    /// Revoked is a sink on purpose: a revoked device must never be accepted
    /// again, and reinstalling generates a new key, which registers as a new
    /// device. Expired can go back to Pending, because a device that simply sat
    /// too long without anyone activating it has done nothing wrong - it just
    /// needs a fresh code.
    /// </remarks>
    public static readonly StateMachine<DeviceStatus> Transitions =
        new StateMachine<DeviceStatus>(nameof(KioskDevice))
            .Allow(DeviceStatus.Pending, DeviceStatus.Active, DeviceStatus.Expired, DeviceStatus.Revoked)
            .Allow(DeviceStatus.Active, DeviceStatus.Revoked)
            .Allow(DeviceStatus.Expired, DeviceStatus.Pending, DeviceStatus.Revoked);

    /// <summary>
    /// Activates the device against a kiosk.
    /// </summary>
    /// <exception cref="DomainException">The device is not in a state that can be activated.</exception>
    public void Activate(Guid kioskId, ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Status == DeviceStatus.Revoked)
        {
            throw new DomainException("This device has been revoked and cannot be activated again. " +
                                      "Reinstall the kiosk application so it registers a new key.");
        }

        if (Status == DeviceStatus.Active && KioskId == kioskId)
        {
            // Activating the device it is already bound to is a no-op rather
            // than an error: an admin may well press the button twice.
            return;
        }

        Record(Transitions.Transition(Id, Status, DeviceStatus.Active, actor, utcNow));

        Status = DeviceStatus.Active;
        KioskId = kioskId;
        ActivatedBy = actor.ActorId;
        ActivatedAt = utcNow;

        // The code is single-use.
        PairingCodeHash = null;
        PairingCodeExpiresAt = null;
    }

    /// <summary>Revokes the device. Its next request is rejected.</summary>
    /// <remarks>
    /// Revoking an already revoked device is a no-op: the panel may send the
    /// command twice, and the device is no less revoked for it.
    /// </remarks>
    public void Revoke(ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Status == DeviceStatus.Revoked)
        {
            return;
        }

        Record(Transitions.Transition(Id, Status, DeviceStatus.Revoked, actor, utcNow));

        Status = DeviceStatus.Revoked;
        RevokedBy = actor.ActorId;
        RevokedAt = utcNow;
        RevokeReason = actor.Reason;
        LastKioskId = KioskId ?? LastKioskId;
        KioskId = null;
        PairingCodeHash = null;
        PairingCodeExpiresAt = null;
    }

    /// <summary>
    /// Puts the device back on the activation screen with a fresh pairing code.
    /// </summary>
    /// <remarks>
    /// What happens when a device registers again: either it has never been
    /// activated, or nobody activated it in time and it expired. A revoked
    /// device does not come through here - the caller refuses it outright.
    /// </remarks>
    /// <exception cref="DomainException">The device is revoked or already active.</exception>
    public void AwaitActivation(string pairingCodeHash, DateTime expiresAt, ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Record(Transitions.Transition(Id, Status, DeviceStatus.Pending, actor, utcNow));

        Status = DeviceStatus.Pending;
        PairingCodeHash = pairingCodeHash;
        PairingCodeExpiresAt = expiresAt;
    }

    /// <summary>
    /// Expires a device nobody activated within the pending window.
    /// </summary>
    /// <remarks>
    /// The code it is showing is worthless once this happens, so it is cleared
    /// rather than left to look valid.
    /// </remarks>
    public void Expire(ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Status == DeviceStatus.Expired)
        {
            return;
        }

        Record(Transitions.Transition(Id, Status, DeviceStatus.Expired, actor, utcNow));

        Status = DeviceStatus.Expired;
        PairingCodeHash = null;
        PairingCodeExpiresAt = null;
    }

    /// <summary>Whether this device may currently hold a kiosk token.</summary>
    public bool CanIssueKioskToken => Status == DeviceStatus.Active && KioskId is not null;
}

/// <summary>
/// A single-use enrollment token for zero-touch activation (plan section 7.11).
/// </summary>
/// <remarks>
/// An admin creates the token up front and hands the provisioning script the
/// matching command. The device then activates itself on first registration,
/// so nobody has to read a code off the screen.
/// </remarks>
public sealed class EnrollmentToken : Entity
{
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The kiosk the device should be bound to, when it already exists.</summary>
    public Guid? KioskId { get; set; }

    /// <summary>Parameters for a kiosk to be created on first use, when KioskId is null.</summary>
    public NewKioskTemplate? NewKioskTemplate { get; set; }

    public DateTime ExpiresAt { get; set; }

    public int MaxUses { get; set; } = 1;

    public Guid? UsedByDeviceId { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTime? RevokedAt { get; set; }

    public bool IsUsable(DateTime utcNow)
        => RevokedAt is null && UsedByDeviceId is null && ExpiresAt > utcNow;

    /// <summary>Generates the token itself; only its hash is stored.</summary>
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

/// <summary>Kiosk parameters filled in ahead of a zero-touch activation.</summary>
public sealed class NewKioskTemplate
{
    public string Name { get; set; } = string.Empty;

    public string? Code { get; set; }

    public KioskLocation Location { get; set; } = new();

    public Guid? PricingProfileId { get; set; }

    public int? MaxFileSizeMb { get; set; }

    public int? MaxPages { get; set; }

    public int? QueueDisplayLimit { get; set; }

    public string DefaultLanguage { get; set; } = "az";

    public string? FiscalTerminalId { get; set; }

    public Dictionary<string, int> PaperCapacity { get; set; } = new(StringComparer.Ordinal);

    /// <summary>PBKDF2 hash of the kiosk admin PIN.</summary>
    public string? AdminPinHash { get; set; }
}
