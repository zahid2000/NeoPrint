namespace NeoPrint.Kiosk.Core;

/// <summary>
/// The kiosk's local configuration file.
/// </summary>
/// <remarks>
/// <para>
/// Plan section 7.11: the kiosk build is universal. This file is part of the
/// build and is <b>identical on every kiosk</b> - it holds no identity, no
/// secret and no per-kiosk parameter. Everything about a particular kiosk
/// arrives from the backend after activation.
/// </para>
/// <para>
/// <see cref="ApiBaseUrl"/> identifies the environment, not the kiosk, and the
/// install script may override it for a test rig. It is not a secret.
/// </para>
/// </remarks>
public sealed class KioskSettings
{
    /// <summary>
    /// UI crashes within ten minutes that start the kiosk in safe mode
    /// (<c>Kiosk.CrashLoopThreshold</c>, plan section 2.4). Read before the
    /// backend is reached, so it lives here rather than in the configuration.
    /// </summary>
    public int CrashLoopThreshold { get; set; } = 3;

    /// <summary>
    /// Whether the application locks the keyboard down itself (plan section
    /// 7.12, the in-process layer). Off in Development, so a developer's
    /// Windows key keeps working.
    /// </summary>
    public bool Lockdown { get; set; } = true;

    /// <summary>
    /// Linux: the account converters run as, without network
    /// (<c>neoprint-convert</c>; set by the provisioning script, docs/TASKS.md
    /// T-075). Empty runs them as the application, under prlimit only.
    /// </summary>
    public string ConverterUser { get; set; } = string.Empty;

    /// <summary>
    /// An enrollment token given as <c>--enrollment-token</c>, for a developer
    /// machine that was not provisioned. A provisioned kiosk finds its token in
    /// a file the script leaves behind (readable by the kiosk user only), and
    /// that file wins when both are present (plan section 7.11, docs/TASKS.md
    /// T-080).
    /// </summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>Backend address for this environment.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.neoprint.az";

    /// <summary>
    /// Prints to a folder instead of a printer, so the whole flow can be
    /// exercised on a machine with no printer and in CI. Always false in a
    /// release build (plan section 7.6).
    /// </summary>
    public bool UseFakePrinter { get; set; }

    /// <summary>LibreOffice binary; empty means the platform default.</summary>
    public string LibreOfficePath { get; set; } = string.Empty;

    /// <summary>How often the kiosk reports that it is alive.</summary>
    public int HeartbeatSeconds { get; set; } = 15;

    /// <summary>
    /// How often an unactivated kiosk re-checks whether it has been activated,
    /// as a fallback when the SignalR notification does not arrive.
    /// </summary>
    public int ActivationPollSeconds { get; set; } = 10;

    /// <summary>Interface language before the backend says otherwise.</summary>
    public string DefaultLanguage { get; set; } = "az";

    /// <summary>The Ghostscript folder on Windows; empty searches PATH (plan section 7.6).</summary>
    public string GhostscriptFolder { get; set; } = string.Empty;

    /// <summary>
    /// Development only: orders are queued without a payment, which arrives in
    /// step 4. The backend refuses it anywhere else; the flash flow is not
    /// offered at all while this is off and there is no payment screen
    /// (docs/DESIGN_SYSTEM.md, "Hazır olmayan funksiya").
    /// </summary>
    public bool SkipPayment { get; set; }
}
