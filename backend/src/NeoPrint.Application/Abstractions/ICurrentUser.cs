namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Who is making the current request.
/// </summary>
/// <remarks>
/// Implemented over the HTTP context. Handlers depend on this rather than on
/// ASP.NET Core, so they can be tested without one.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>The registered mobile user, when a user token was presented.</summary>
    Guid? UserId { get; }

    /// <summary>The administrator or operator, when an admin token was presented.</summary>
    Guid? AdminUserId { get; }

    /// <summary>The kiosk, when a kiosk token was presented.</summary>
    Guid? KioskId { get; }

    /// <summary>The device, when a provisioning or kiosk token was presented.</summary>
    Guid? DeviceId { get; }

    /// <summary>Caller IP, recorded on consents and audit entries.</summary>
    string? Ip { get; }
}
