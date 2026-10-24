using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Common;

/// <summary>
/// The body is larger than this endpoint takes (413): the caller has to send
/// less, not try the same again.
/// </summary>
public sealed class PayloadTooLargeException : Exception
{
    public PayloadTooLargeException(UserMessage text)
        : base(text?.Key)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>What to tell the caller.</summary>
    public UserMessage Text { get; }
}
