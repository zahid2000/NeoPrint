using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Kiosks;

/// <summary>
/// Lifecycle of a kiosk (plan sections 5, 7.11).
/// </summary>
public enum KioskStatus
{
    /// <summary>The kiosk record exists but no device is bound to it.</summary>
    NotActivated = 0,

    /// <summary>A device is bound, but no printer has been selected yet.</summary>
    NotConfigured = 1,

    Online = 2,
    Paused = 3,
    Offline = 4,
    Maintenance = 5,
    Disabled = 6,
}

/// <summary>Which operating system the bound device runs.</summary>
public enum KioskPlatform
{
    Unknown = 0,
    Windows = 1,
    Linux = 2,
}

/// <summary>Where a kiosk stands.</summary>
public sealed class KioskLocation
{
    public double Lat { get; set; }

    public double Lng { get; set; }

    public string Address { get; set; } = string.Empty;
}

/// <summary>Paper counter for one paper size (plan section 2.15).</summary>
public sealed class PaperStock
{
    /// <summary>Tray capacity in sheets, set by an admin.</summary>
    public int Capacity { get; set; }

    /// <summary>Sheets currently loaded, decremented as jobs complete.</summary>
    public int Current { get; set; }

    public DateTime? LastRefillAt { get; set; }

    public Guid? LastRefillBy { get; set; }
}

/// <summary>What a printer can do, as detected or as overridden by an admin.</summary>
public sealed class PrinterCapabilities
{
    public bool Color { get; set; }

    public bool Duplex { get; set; }

    public bool A3 { get; set; }
}

/// <summary>An admin override of a detected capability; null means "use what was detected".</summary>
public sealed class CapabilityOverrides
{
    public bool? Color { get; set; }

    public bool? Duplex { get; set; }

    public bool? A3 { get; set; }
}

/// <summary>A printer the kiosk reported (plan section 7.7).</summary>
public sealed class AvailablePrinter
{
    public string Name { get; set; } = string.Empty;

    public string? DriverName { get; set; }

    public bool IsOnline { get; set; }

    public PrinterCapabilities DetectedCapabilities { get; set; } = new();

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Which printer an admin chose as the active one.</summary>
public sealed class SelectedPrinter
{
    public string Name { get; set; } = string.Empty;

    public Guid? SelectedBy { get; set; }

    public DateTime SelectedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A kiosk (plan section 5, <c>kiosks</c>).
/// </summary>
/// <remarks>
/// The kiosk record holds everything about a site: code, location, limits,
/// pricing, paper and the admin PIN. The device bound to it is interchangeable
/// - replacing broken hardware keeps the code, the history and the counters
/// (plan section 7.11).
/// </remarks>
public sealed class Kiosk : Entity
{
    /// <summary><c>KSK-0001</c>. Printed on the kiosk QR and quoted in support calls.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public KioskLocation Location { get; set; } = new();

    /// <summary>
    /// Where the kiosk is in its lifecycle. Changed only through the transition
    /// methods below (<c>docs/STATE_MACHINES.md</c>).
    /// </summary>
    public KioskStatus Status { get; private set; } = KioskStatus.NotActivated;

    /// <summary>The device currently allowed to act as this kiosk.</summary>
    public Guid? ActiveDeviceId { get; set; }

    /// <summary>
    /// PBKDF2 hash of the PIN that unlocks the hidden admin exit on the kiosk
    /// screen. Only the hash ever reaches the device (plan sections 7.1, 2.13).
    /// </summary>
    public string? AdminPinHash { get; set; }

    public string DefaultLanguage { get; set; } = "az";

    public KioskPlatform Platform { get; set; } = KioskPlatform.Unknown;

    public string? AppVersion { get; set; }

    public List<AvailablePrinter> AvailablePrinters { get; set; } = [];

    public SelectedPrinter? SelectedPrinter { get; set; }

    public CapabilityOverrides CapabilityOverrides { get; set; } = new();

    /// <summary>Queue entries shown on screen; null falls back to Queue.DisplayLimit.</summary>
    public int? QueueDisplayLimit { get; set; }

    /// <summary>Null falls back to Files.DefaultMaxSizeMb (plan section 2.2).</summary>
    public int? MaxFileSizeMb { get; set; }

    /// <summary>Null falls back to Files.DefaultMaxPages.</summary>
    public int? MaxPages { get; set; }

    /// <summary>Null falls back to the default pricing profile (plan section 2.3).</summary>
    public Guid? PricingProfileId { get; set; }

    /// <summary>Paper counters by size: "A4", "A3".</summary>
    public Dictionary<string, PaperStock> PaperStock { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Fiscal terminal registered for this kiosk (plan section 2.12).</summary>
    public string? FiscalTerminalId { get; set; }

    public DateTime? LastHeartbeatAt { get; set; }

    /// <summary>
    /// What the kiosk may actually offer: the detected capabilities of the
    /// selected printer, with any admin override applied.
    /// </summary>
    public PrinterCapabilities EffectiveCapabilities()
    {
        var detected = SelectedPrinter is null
            ? new PrinterCapabilities()
            : AvailablePrinters
                  .FirstOrDefault(printer => printer.Name == SelectedPrinter.Name)?.DetectedCapabilities
              ?? new PrinterCapabilities();

        return new PrinterCapabilities
        {
            Color = CapabilityOverrides.Color ?? detected.Color,
            Duplex = CapabilityOverrides.Duplex ?? detected.Duplex,
            A3 = CapabilityOverrides.A3 ?? detected.A3,
        };
    }

    /// <summary>Generates the next kiosk code from the highest one in use.</summary>
    public static string NextCode(int sequence) => $"KSK-{sequence:D4}";

    /// <summary>
    /// The transitions a kiosk may make (<c>docs/STATE_MACHINES.md</c>).
    /// </summary>
    /// <remarks>
    /// Losing its device returns a kiosk to NotActivated from wherever it was,
    /// so every status leads back there. Disabled is reachable from anywhere -
    /// an administrator can always take a kiosk out of service - and a disabled
    /// kiosk comes back to NotActivated, because whatever device it had is gone
    /// by then.
    /// </remarks>
    public static readonly StateMachine<KioskStatus> Transitions =
        new StateMachine<KioskStatus>(nameof(Kiosk))
            .Allow(KioskStatus.NotActivated, KioskStatus.NotConfigured, KioskStatus.Online)
            .Allow(KioskStatus.NotConfigured, KioskStatus.Online, KioskStatus.NotActivated, KioskStatus.Offline)
            .Allow(
                KioskStatus.Online,
                KioskStatus.Paused,
                KioskStatus.Offline,
                KioskStatus.Maintenance,
                KioskStatus.NotConfigured,
                KioskStatus.NotActivated)
            .Allow(KioskStatus.Paused, KioskStatus.Online, KioskStatus.Offline, KioskStatus.NotActivated)
            .Allow(KioskStatus.Offline, KioskStatus.Online, KioskStatus.Paused, KioskStatus.NotActivated)
            .Allow(KioskStatus.Maintenance, KioskStatus.Online, KioskStatus.NotActivated)
            .Allow(KioskStatus.Disabled, KioskStatus.NotActivated)
            .AllowFromAny(KioskStatus.Disabled);

    /// <summary>
    /// Binds a device and moves the kiosk out of <see cref="KioskStatus.NotActivated"/>.
    /// </summary>
    /// <exception cref="DomainException">The kiosk is disabled.</exception>
    public void BindDevice(
        Guid deviceId, KioskPlatform platform, string? appVersion, ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Status == KioskStatus.Disabled)
        {
            throw new DomainException($"Kiosk {Code} is disabled and cannot have a device activated on it.");
        }

        // A printer still has to be chosen before the kiosk can take orders
        // (plan section 7.7).
        var bound = SelectedPrinter is null ? KioskStatus.NotConfigured : KioskStatus.Online;

        Record(Transitions.Transition(Id, Status, bound, actor, utcNow));

        ActiveDeviceId = deviceId;
        Platform = platform;
        AppVersion = appVersion;
        Status = bound;
    }

    /// <summary>Unbinds the current device; the kiosk keeps everything else.</summary>
    /// <remarks>
    /// The kiosk's identity, code, history and counters survive a machine being
    /// replaced - that is the whole point of keeping kiosk and device apart
    /// (<c>docs/DOMAIN_RULES.md</c>).
    /// </remarks>
    public void UnbindDevice(ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Status == KioskStatus.NotActivated && ActiveDeviceId is null)
        {
            return;
        }

        Record(Transitions.Transition(Id, Status, KioskStatus.NotActivated, actor, utcNow));

        ActiveDeviceId = null;
        Status = KioskStatus.NotActivated;
    }

    /// <summary>
    /// Moves the kiosk to a status an operator or a heartbeat decided on.
    /// </summary>
    /// <remarks>
    /// The one way to change a kiosk's status outside binding a device, so the
    /// transition table governs all of it. Pausing, resuming, going offline
    /// when the heartbeat stops and being taken out of service all come
    /// through here.
    /// </remarks>
    /// <exception cref="IllegalStateTransitionException">The move is not allowed.</exception>
    public void MoveTo(KioskStatus status, ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Record(Transitions.Transition(Id, Status, status, actor, utcNow));

        Status = status;
    }
}
