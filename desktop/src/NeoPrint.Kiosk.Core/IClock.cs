namespace NeoPrint.Kiosk.Core;

/// <summary>
/// The current time, behind an interface.
/// </summary>
/// <remarks>
/// The kiosk decides whether its cached configuration is still fresh by
/// comparing timestamps, and a test of that rule cannot wait twenty-four hours
/// (plan section 7.3). Section 2.0 rules out a clock library, so this is a
/// one-line abstraction of our own.
/// </remarks>
public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
