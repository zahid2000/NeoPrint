using NeoPrint.Domain.Common;
using NeoPrint.Domain.Releases;

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

    /// <summary>
    /// Whether anybody has said how much paper is in the tray. A kiosk is
    /// created with a capacity and a count of nought that nobody counted: until
    /// the first refill is recorded the count is not "empty", it is unknown -
    /// nothing is alerted about it and no task is opened (T-258). Once set it
    /// stays set: a tray that was filled and printed down to nought is empty.
    /// </summary>
    /// <remarks>
    /// Worked out from what is stored, so kiosks from before this rule need no
    /// migration: a recorded refill, or a count somebody seeded.
    /// </remarks>
    public bool IsCounted => LastRefillAt is not null || Current > 0;
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

    /// <summary>The agent's version, from the heartbeat (plan section 7.12).</summary>
    public string? AgentVersion { get; set; }

    /// <summary>The provisioning scripts' version, from the heartbeat (<c>ProvisioningVersion</c>).</summary>
    public string? ProvisioningVersion { get; set; }

    /// <summary>
    /// Which releases reach this kiosk first: pilot kiosks get a release before
    /// it is promoted to everyone (plan section 7.12, step 2).
    /// </summary>
    public ReleaseChannel UpdateChannel { get; set; } = ReleaseChannel.Stable;

    /// <summary>The last update report of each component (<c>POST kiosks/me/update-status</c>).</summary>
    public List<KioskUpdateState> UpdateStates { get; set; } = [];

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
    /// Since when the kiosk UI runs in safe mode after a crash loop (plan
    /// section 7.12); null when it does not. Set and cleared by the kiosk's
    /// heartbeat, through <see cref="EnterSafeMode"/> and <see cref="LeaveSafeMode"/>.
    /// </summary>
    public DateTime? SafeModeSince { get; private set; }

    /// <summary>Whether safe mode took the kiosk from Online to Maintenance, and so gives it back.</summary>
    public bool SafeModeSetMaintenance { get; private set; }

    /// <summary>
    /// Since when an administrator has the kiosk paused (plan section 7.8,
    /// <c>Pause</c>); null when they do not. While it is set the kiosk takes
    /// no new orders and finishes its queue - whatever its connection or its
    /// printer are doing - until <see cref="ResumeByAdmin"/>.
    /// </summary>
    public DateTime? PausedByAdminSince { get; private set; }

    /// <summary>
    /// Since when the kiosk's printer cannot print (plan section 9.6,
    /// scenario A); null when it can. While it is set the kiosk is Paused -
    /// the backend takes no new orders for it - unless something stronger
    /// holds it: an administrator's pause, a lost connection. Set by
    /// <see cref="DeclareFault"/>, lifted by <see cref="ClearFault"/>.
    /// </summary>
    public DateTime? FaultSince { get; private set; }

    /// <summary>What is wrong with the printer, as a message key: <c>printer.outOfPaper</c>.</summary>
    public string? FaultCode { get; private set; }

    /// <summary>
    /// Since when the kiosk's heartbeats have been saying its printer is not
    /// ready, before that counts as a fault: one bad reading is not an outage.
    /// </summary>
    public DateTime? PrinterDownSince { get; private set; }

    /// <summary>
    /// The status of a kiosk that is reachable: Online, unless an
    /// administrator has it paused or its printer cannot print. What a kiosk
    /// goes back to when its heartbeat returns or its test page prints.
    /// </summary>
    public KioskStatus WorkingStatus
        => PausedByAdminSince is not null ? KioskStatus.Maintenance
            : FaultSince is not null ? KioskStatus.Paused
            : KioskStatus.Online;

    /// <summary>
    /// When the kiosk's configuration last changed.
    /// </summary>
    /// <remarks>
    /// Returned with every heartbeat, so a kiosk knows to fetch its
    /// configuration again without polling the whole document. Not touched by
    /// the heartbeat itself - a heartbeat is not a configuration change.
    /// </remarks>
    public DateTime ConfigurationChangedAt { get; set; } = DateTime.UtcNow;

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
            .Allow(
                KioskStatus.NotConfigured,
                KioskStatus.Online,
                KioskStatus.NotActivated,
                KioskStatus.Offline,
                KioskStatus.Maintenance,
                KioskStatus.Paused)
            .Allow(
                KioskStatus.Online,
                KioskStatus.Paused,
                KioskStatus.Offline,
                KioskStatus.Maintenance,
                KioskStatus.NotConfigured,
                KioskStatus.NotActivated)
            .Allow(KioskStatus.Paused, KioskStatus.Online, KioskStatus.Offline, KioskStatus.NotActivated, KioskStatus.Maintenance)
            .Allow(KioskStatus.Offline, KioskStatus.Online, KioskStatus.Paused, KioskStatus.NotActivated, KioskStatus.Maintenance)
            .Allow(KioskStatus.Maintenance, KioskStatus.Online, KioskStatus.NotActivated, KioskStatus.NotConfigured, KioskStatus.Paused)
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
            throw new DomainException(UserMessage.Of("kiosks.disabledNoDevice", ("code", Code)));
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
    /// Sheets came out of the tray: a job's or a test page's (plan section
    /// 2.15). The counter does not go below zero - a counter that says less
    /// than the tray holds is corrected by a recount, not by a negative number.
    /// </summary>
    /// <returns>The movement to record, or null when this paper size has no counter or nothing was used.</returns>
    /// <param name="run">
    /// Which run of the job this is (<c>PrintJob.Runs</c>): a job that stopped
    /// at the printer and printed again used paper twice, and both count.
    /// </param>
    public SupplyEvent? UsePaper(
        string paper, int sheets, SupplyEventType type, DateTime utcNow, Guid? jobId = null, Guid? commandId = null, int run = 1)
    {
        if (sheets <= 0 || !PaperStock.TryGetValue(paper, out var stock))
        {
            return null;
        }

        var before = stock.Current;
        stock.Current = Math.Max(0, stock.Current - sheets);
        ConfigurationChangedAt = utcNow;

        return new SupplyEvent
        {
            KioskId = Id,
            Resource = SupplyEvent.PaperResource(paper),
            Type = type,
            Delta = stock.Current - before,
            Before = before,
            After = stock.Current,
            JobId = jobId,
            CommandId = commandId,
            At = utcNow,
            DedupKey = jobId is { } job ? (run > 1 ? $"job:{job:N}:{run}" : $"job:{job:N}")
                : commandId is { } command ? $"command:{command:N}"
                : Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>An operator put paper in the tray, or counted it (plan section 2.15).</summary>
    /// <exception cref="DomainException">The size has no tray, or the number does not fit it.</exception>
    public SupplyEvent RefillPaper(string paper, PaperRefillMode mode, int sheets, Guid by, DateTime utcNow)
    {
        if (!PaperStock.TryGetValue(paper, out var stock) || stock.Capacity <= 0)
        {
            throw new DomainException(UserMessage.Of("kiosks.paperNoTray", ("paper", paper)));
        }

        var after = mode switch
        {
            PaperRefillMode.Full => stock.Capacity,
            PaperRefillMode.Add => stock.Current + sheets,
            _ => sheets,
        };

        if ((mode != PaperRefillMode.Full && sheets < 0) || (mode == PaperRefillMode.Add && sheets == 0) || after > stock.Capacity)
        {
            throw new DomainException(UserMessage.Of("kiosks.paperRefillInvalid", ("capacity", stock.Capacity), ("current", stock.Current)));
        }

        var before = stock.Current;
        stock.Current = after;
        stock.LastRefillAt = utcNow;
        stock.LastRefillBy = by;
        ConfigurationChangedAt = utcNow;

        return new SupplyEvent
        {
            KioskId = Id,
            Resource = SupplyEvent.PaperResource(paper),
            Type = mode == PaperRefillMode.Exact ? SupplyEventType.Recount : SupplyEventType.Refill,
            Delta = after - before,
            Before = before,
            After = after,
            By = by,
            At = utcNow,
            DedupKey = $"refill:{Guid.NewGuid():N}",
        };
    }

    /// <summary>
    /// The kiosk UI started in safe mode (plan section 7.12): it takes no
    /// orders, so a kiosk that was Online shows Maintenance - on the map and
    /// in the panel - rather than a kiosk that looks open and is not.
    /// </summary>
    /// <remarks>
    /// Paused, NotConfigured and an administrator's Maintenance keep their
    /// status; the flag still tells the panel why nothing is printing.
    /// </remarks>
    public void EnterSafeMode(ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (SafeModeSince is not null)
        {
            return;
        }

        SafeModeSince = utcNow;

        if (Status == KioskStatus.Online)
        {
            MoveTo(KioskStatus.Maintenance, actor.Because("kiosks.safeMode"), utcNow);
            SafeModeSetMaintenance = true;
        }
    }

    /// <summary>
    /// Safe mode was cleared on the kiosk: it takes orders again. Only the
    /// Maintenance that safe mode brought is undone - one an administrator
    /// chose stays until they lift it.
    /// </summary>
    public void LeaveSafeMode(ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (SafeModeSince is null)
        {
            return;
        }

        // An administrator's pause outlives safe mode: PauseByAdmin took the
        // maintenance over, so there is nothing of safe mode's own to undo.
        var ownMaintenance = SafeModeSetMaintenance && Status == KioskStatus.Maintenance && PausedByAdminSince is null;

        SafeModeSince = null;
        SafeModeSetMaintenance = false;

        if (ownMaintenance)
        {
            // Back to work - or to Paused, when the printer is still down.
            MoveTo(WorkingStatus, actor.Because("kiosks.safeModeCleared"), utcNow);
        }
    }

    /// <summary>A heartbeat said the printer is not ready.</summary>
    /// <returns>Since when the heartbeats have been saying so.</returns>
    public DateTime NotePrinterDown(DateTime utcNow) => PrinterDownSince ??= utcNow;

    /// <summary>A heartbeat said the printer is ready, with no fault declared: the bad readings were a blip.</summary>
    public void NotePrinterUp() => PrinterDownSince = null;

    /// <summary>
    /// The printer cannot print (plan section 9.6, scenario A): an Online
    /// kiosk becomes Paused. A kiosk an administrator has paused, or one that
    /// is offline, keeps its status and remembers the fault - it is Paused
    /// when that ends and the printer is still down.
    /// </summary>
    /// <param name="code">What is wrong, as a message key.</param>
    /// <returns>True when the fault begins now; false when one was declared already (its code is brought up to date).</returns>
    public bool DeclareFault(string code, ActorContext actor, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(actor);

        FaultCode = code;
        PrinterDownSince ??= utcNow;

        if (FaultSince is not null)
        {
            return false;
        }

        FaultSince = utcNow;

        if (Status == KioskStatus.Online)
        {
            MoveTo(KioskStatus.Paused, actor.Because(code), utcNow);
        }

        return true;
    }

    /// <summary>The printer prints again: a kiosk the fault had Paused is Online.</summary>
    /// <returns>False when no fault was declared.</returns>
    public bool ClearFault(ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        PrinterDownSince = null;

        if (FaultSince is null)
        {
            return false;
        }

        FaultSince = null;
        FaultCode = null;

        // Paused is the fault's own status: an administrator's pause is
        // Maintenance, and a lost connection is Offline until a heartbeat.
        if (Status == KioskStatus.Paused)
        {
            MoveTo(KioskStatus.Online, actor.Because("kiosks.reason.printerRecovered"), utcNow);
        }

        return true;
    }

    /// <summary>
    /// An administrator pauses the kiosk (plan section 7.8): from now it takes
    /// no new orders - the backend refuses them at once, also for a kiosk that
    /// is offline and has not heard yet - and the queue is finished.
    /// </summary>
    /// <returns>False when it was paused already.</returns>
    public bool PauseByAdmin(ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (PausedByAdminSince is not null)
        {
            return false;
        }

        PausedByAdminSince = utcNow;
        ConfigurationChangedAt = utcNow;

        // The maintenance is the administrator's from here on: the kiosk
        // leaving safe mode does not open it again.
        SafeModeSetMaintenance = false;

        if (Status is KioskStatus.Online or KioskStatus.Paused or KioskStatus.Offline)
        {
            MoveTo(KioskStatus.Maintenance, actor.Because("kiosks.reason.pausedByAdmin"), utcNow);
        }

        return true;
    }

    /// <summary>
    /// The administrator lifts their pause. A kiosk still in safe mode stays
    /// in maintenance - that is now safe mode's again - and one without a
    /// printer goes back to waiting for one.
    /// </summary>
    /// <returns>False when it was not paused.</returns>
    public bool ResumeByAdmin(ActorContext actor, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (PausedByAdminSince is null)
        {
            return false;
        }

        PausedByAdminSince = null;
        ConfigurationChangedAt = utcNow;

        if (Status != KioskStatus.Maintenance)
        {
            return true;
        }

        if (SafeModeSince is not null)
        {
            SafeModeSetMaintenance = true;
            return true;
        }

        // WorkingStatus: Online, or Paused when the printer went down meanwhile.
        MoveTo(
            SelectedPrinter is null ? KioskStatus.NotConfigured : WorkingStatus,
            actor.Because("kiosks.reason.resumedByAdmin"),
            utcNow);

        return true;
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

    /// <summary>
    /// The kiosk says where an update of one component stands. Only the latest
    /// report of each component is kept; a report older than the one kept -
    /// one that arrived late - changes nothing.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public bool ReportUpdate(ReleaseComponent component, string version, KioskUpdateStatus status, string? messageKey, DateTime reportedAt)
    {
        var current = UpdateStates.FirstOrDefault(state => state.Component == component);
        if (current is not null && (current.ReportedAt > reportedAt
                                    || (current.ReportedAt == reportedAt && current.Version == version && current.Status == status)))
        {
            return false;
        }

        UpdateStates.RemoveAll(state => state.Component == component);
        UpdateStates.Add(new KioskUpdateState
        {
            Component = component,
            Version = version,
            Status = status,
            MessageKey = messageKey,
            ReportedAt = reportedAt,
        });
        return true;
    }
}

/// <summary>
/// Where an update of one component stands on a kiosk (plan section 7.12,
/// steps 3-5; docs/STATE_MACHINES.md "Update"). <c>Idle</c> is never reported.
/// </summary>
public enum KioskUpdateStatus
{
    Downloading = 0,

    /// <summary>Downloaded and checked; waiting for the update window and an idle kiosk.</summary>
    Ready = 1,

    /// <summary>The switch to the new version is being made.</summary>
    Applying = 2,

    /// <summary>The new version runs; the health check is not over yet.</summary>
    HealthCheck = 3,

    /// <summary>Running, and the health check passed.</summary>
    Succeeded = 4,

    /// <summary>Refused (signature, hash, version) or could not be applied; nothing changed on the kiosk.</summary>
    Failed = 5,

    /// <summary>Applied, failed its health check and was taken back.</summary>
    RolledBack = 6,
}

public sealed class KioskUpdateState
{
    public ReleaseComponent Component { get; set; }

    public string Version { get; set; } = string.Empty;

    public KioskUpdateStatus Status { get; set; }

    /// <summary>What happened, as a message key (<c>updates.signatureRefused</c>).</summary>
    public string? MessageKey { get; set; }

    public DateTime ReportedAt { get; set; }
}
