namespace NeoPrint.Contracts;

/// <summary>
/// An incident was opened - of any kind (plan section 6, events). The people
/// who look after the system are told by e-mail and Telegram.
/// </summary>
public sealed record IncidentRaised(Guid IncidentId, string Type, string Severity, Guid? KioskId, string? CorrelationId);

/// <summary>
/// A kiosk's printer stopped and the kiosk said so (plan section 9.6,
/// scenario A): the paid orders waiting there are refunded or their owners
/// are offered a refund.
/// </summary>
/// <param name="Code">What is wrong, as a message key: <c>printer.outOfPaper</c>.</param>
public sealed record KioskIncidentRaised(Guid? IncidentId, Guid KioskId, string Type, string Code, string? CorrelationId);

/// <summary>A kiosk stopped calling in (plan section 9.6, scenario B).</summary>
public sealed record KioskWentOffline(Guid KioskId, DateTime? LastHeartbeatAt, string? CorrelationId);
