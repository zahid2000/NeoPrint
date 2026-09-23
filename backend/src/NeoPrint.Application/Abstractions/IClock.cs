namespace NeoPrint.Application.Abstractions;

/// <summary>
/// The current time, injected so that handlers dealing with expiry, lockouts
/// and schedules can be tested deterministically.
/// </summary>
/// <remarks>
/// Always UTC: the server stores UTC and the UI renders Asia/Baku
/// (plan section 2.1).
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
