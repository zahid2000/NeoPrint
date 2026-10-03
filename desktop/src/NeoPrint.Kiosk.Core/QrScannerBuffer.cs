using System.Text;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// The kiosk's QR reader is a USB HID scanner that types what it reads like a
/// keyboard (plan section 2.0, "QR reader"): this tells its typing from a
/// person's. A read is a line whose characters arrive less than
/// <see cref="MaxGap"/> apart and that ends with Enter (CR, or LF where the
/// scanner is set up so). Same on both systems.
/// </summary>
/// <remarks>Not thread safe: fed from the UI thread's key events.</remarks>
public sealed class QrScannerBuffer
{
    /// <summary>A scanner types faster than this between characters; a person does not.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromMilliseconds(50);

    /// <summary>Shorter lines are noise, not a code we know.</summary>
    public const int MinLength = 8;

    /// <summary>Longer than any code we print; the rest is dropped.</summary>
    public const int MaxLength = 512;

    private readonly StringBuilder _line = new();
    private TimeSpan _last;

    /// <summary>A typed character, at <paramref name="at"/> on a monotonic clock.</summary>
    public void Accept(char character, TimeSpan at)
    {
        if (char.IsControl(character))
        {
            return;
        }

        // Too slow after the previous one: a new line starts here.
        if (_line.Length > 0 && at - _last > MaxGap)
        {
            _line.Clear();
        }

        if (_line.Length < MaxLength)
        {
            _line.Append(character);
        }

        _last = at;
    }

    /// <summary>Enter: the line, if it was a scanner's; null otherwise. Either way the line is over.</summary>
    public string? Enter(TimeSpan at)
    {
        var line = _line.Length >= MinLength && at - _last <= MaxGap ? _line.ToString() : null;
        _line.Clear();
        return line;
    }
}
