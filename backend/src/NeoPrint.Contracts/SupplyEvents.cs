namespace NeoPrint.Contracts;

/// <summary>
/// A kiosk's paper counter moved - sheets were printed, a tray was refilled or
/// counted - or a cartridge was recorded as changed (plan section 9.9, step
/// 1). The supplies are judged against their rules again.
/// </summary>
public sealed record SupplyLevelChanged(Guid KioskId, string? CorrelationId);
