namespace NeoPrint.Contracts;

/// <summary>
/// A notification was stored for a user (plan section 8.4); delivering it - the
/// app's hub and a push to each device - starts here.
/// </summary>
public sealed record UserNotificationCreated(Guid NotificationId, Guid UserId, string Type, string? CorrelationId);
