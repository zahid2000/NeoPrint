namespace NeoPrint.Kiosk.Core.Camera;

/// <summary>
/// The kiosk camera, as the configuration carries it (<c>camera</c>, T-482).
/// The backend sends it only to a kiosk the <c>kiosk_camera</c> flag is on
/// for: no camera section means "do not record".
/// </summary>
/// <param name="Mode"><see cref="Activity"/> or <see cref="Continuous"/>.</param>
/// <param name="RetentionDays">How long recordings stay on the disk (<c>Camera.RetentionDays</c>).</param>
/// <param name="MaxDiskGb">The most disk they may take; the oldest go first.</param>
/// <param name="Resolution"><c>640x480</c>, <c>1280x720</c> or <c>1920x1080</c>.</param>
/// <param name="KeyId">The backend's camera key, named in every sealed segment.</param>
/// <param name="PublicKey">Its public half, X.509 SubjectPublicKeyInfo (DER), base64: the kiosk can seal, never open.</param>
/// <param name="Quality"><c>Low</c>, <c>Medium</c> or <c>High</c> (<c>Camera.Quality</c>, T-486).</param>
public sealed record KioskCameraSettings(
    string Mode = KioskCameraSettings.Activity,
    int RetentionDays = 2,
    int MaxDiskGb = 20,
    string Resolution = "1280x720",
    int Fps = 10,
    string? KeyId = null,
    string? PublicKey = null,
    string Quality = "Medium")
{
    /// <summary>Only while somebody is at the kiosk, <see cref="CameraVault.ActivityMargin"/> either side.</summary>
    public const string Activity = "Activity";

    public const string Continuous = "Continuous";

    /// <summary>The backend's limits, kept here too: a changed backend cannot ask for a year of video.</summary>
    public int Days => Math.Clamp(RetentionDays, 1, 31);

    public long MaxBytes => Math.Clamp(MaxDiskGb, 1, 500) * 1024L * 1024 * 1024;

    public int FramesPerSecond => Math.Clamp(Fps, 2, 30);

    public bool RecordsAlways => string.Equals(Mode, Continuous, StringComparison.Ordinal);

    /// <summary>The quality as FFmpeg is asked for it; an unknown word is the middle one.</summary>
    public string QualityLevel => Quality is "Low" or "High" ? Quality : "Medium";

    /// <summary>Width and height; the default when the text is not one of the three sizes.</summary>
    public (int Width, int Height) Size => Resolution switch
    {
        "640x480" => (640, 480),
        "1920x1080" => (1920, 1080),
        _ => (1280, 720),
    };
}
