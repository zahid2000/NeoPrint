using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Common;

/// <summary>
/// The account sees the thing but may not change it: it also concerns kiosks
/// outside the account's scope (T-295).
/// </summary>
/// <remarks>
/// Surfaces as 403 with a text that says what to do instead. What the account
/// may not see at all is a <see cref="NotFoundException"/>, as everywhere else.
/// </remarks>
public sealed class OutsideScopeException : Exception
{
    public OutsideScopeException(string key)
        : base(key)
    {
        Text = new UserMessage(key);
    }

    /// <summary>What to tell the caller.</summary>
    public UserMessage Text { get; }
}
