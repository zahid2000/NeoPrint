using System.Security.Cryptography;
using System.Text;

namespace NeoPrint.Domain.Users;

/// <summary>
/// One device that receives a user's push notifications: its FCM registration
/// token and the language the app shows there.
/// </summary>
/// <remarks>
/// The language is the device's, sent with the token, not the account's: the
/// app follows the phone's language, so a push reads the way the app does on
/// the phone it lands on (<c>docs/DECISIONS.md</c>, T-146).
/// </remarks>
public sealed class PushDevice
{
    /// <summary>The FCM registration token. A credential for that device: never logged.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>az, en or ru.</summary>
    public string Language { get; set; } = "az";

    /// <summary><c>android</c>, <c>ios</c> or <c>web</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    public DateTime RegisteredAt { get; set; }

    /// <summary>The longest a registration token is taken to be (FCM's are about 160-200 characters).</summary>
    public const int MaxTokenLength = 4096;

    /// <summary>What a log line or a "reached" list may carry instead of the token.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16].ToLowerInvariant();
    }
}
