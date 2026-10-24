namespace NeoPrint.Kiosk.Core.Updates;

/// <summary>
/// The screen as the update coordinator sees it, before and after there is
/// one: a kiosk not activated yet has no screen to turn customers away from,
/// and is not idle in any sense an update could use.
/// </summary>
public sealed class UpdateHostProxy : IUpdateHost
{
    /// <summary>The activated kiosk's screen; set while a session runs.</summary>
    public IUpdateHost? Target { get; set; }

    public bool IsIdle => Target?.IsIdle ?? false;

    public void SetDraining(bool draining) => Target?.SetDraining(draining);

    public void SetUpdating(bool updating) => Target?.SetUpdating(updating);
}
