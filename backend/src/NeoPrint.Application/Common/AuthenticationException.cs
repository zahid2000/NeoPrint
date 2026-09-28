using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Common;

/// <summary>
/// The caller is not signed in, or the credential they presented is no longer
/// good.
/// </summary>
/// <remarks>
/// <para>
/// Surfaces as 401, which is what "who you are could not be established"
/// means. A rejected refresh token used to come back as a 400 validation
/// problem, which told a client that its request was malformed when in fact
/// its session had ended - and a generic client would have retried it rather
/// than signing the person in again.
/// </para>
/// <para>
/// Deliberately carries one message and no field: a refresh failure must not
/// say whether the token was unknown, expired, already rotated or belonged to
/// a deactivated account. That distinction is useful to an attacker and to
/// nobody else; the log keeps the detail.
/// </para>
/// </remarks>
public sealed class AuthenticationException : Exception
{
    public AuthenticationException(string key)
        : base(key)
    {
        Text = new UserMessage(key);
    }

    /// <summary>What to tell the caller.</summary>
    public UserMessage Text { get; }
}
