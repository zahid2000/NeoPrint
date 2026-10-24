using Microsoft.Extensions.Configuration;

namespace NeoPrint.Api.Infrastructure;

/// <summary>
/// The request rate limits (plan section 6).
/// </summary>
/// <remarks>
/// The defaults are the documented ones. They are read from configuration
/// rather than hard-coded so operations can tune them under load without a code
/// change - and so the integration tests, which come from a single address, can
/// raise them instead of measuring the limiter by accident.
/// </remarks>
public sealed class RateLimitSettings
{
    /// <summary>Auth endpoints: 10 per minute per IP.</summary>
    public int AuthPerMinute { get; init; } = 10;

    /// <summary>
    /// Anonymous device registration: 10 per hour per IP
    /// (plan section 7.11).
    /// </summary>
    public int DeviceRegisterPerHour { get; init; } = 10;

    /// <summary>
    /// Token refreshes per minute per IP. Higher than the credential limit:
    /// a refresh carries a 256-bit server-issued token, which cannot be
    /// guessed, and many people share an IP behind an office or mobile NAT
    /// (docs/DECISIONS.md).
    /// </summary>
    public int RefreshPerMinute { get; init; } = 60;

    /// <summary>
    /// Anonymous support claims per hour per IP: the job and kiosk codes on a
    /// receipt are short, so trying pairs is kept slow.
    /// </summary>
    public int SupportClaimPerHour { get; init; } = 10;

    /// <summary>
    /// Registrations with one enrollment token per hour (T-309): each kiosk has
    /// a token of its own, so kiosks behind one router do not share this.
    /// </summary>
    public int EnrollmentTokenPerHour { get; init; } = 5;

    /// <summary>Everything else: 120 per minute per caller.</summary>
    public int GlobalPerMinute { get; init; } = 120;

    /// <summary>
    /// Turns the limiter off entirely. Only ever set in tests; the limits are
    /// part of the service's protection in every other environment.
    /// </summary>
    public bool Disabled { get; init; }

    public static RateLimitSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("RateLimits");

        return new RateLimitSettings
        {
            AuthPerMinute = section.GetValue("AuthPerMinute", 10),
            DeviceRegisterPerHour = section.GetValue("DeviceRegisterPerHour", 10),
            RefreshPerMinute = section.GetValue("RefreshPerMinute", 60),
            SupportClaimPerHour = section.GetValue("SupportClaimPerHour", 10),
            GlobalPerMinute = section.GetValue("GlobalPerMinute", 120),
            EnrollmentTokenPerHour = section.GetValue("EnrollmentTokenPerHour", 5),
            Disabled = section.GetValue("Disabled", false),
        };
    }
}
