namespace NeoPrint.Domain.Analytics;

/// <summary>A name and how many times it came up: a format, a reason, a screen.</summary>
public sealed record NamedCount(string Key, int Count);

/// <summary>How many files of one format were chosen, and how many of them would not open.</summary>
public sealed record FormatCount(string Format, int Selected, int Failed);

/// <summary>How many payments of one method were started, and how many went through.</summary>
public sealed record MethodCount(string Method, int Started, int Succeeded);

/// <summary>
/// A customer's way through a kiosk, from the flash drive to the printed
/// sheet (plan section 2.17, "ekran-ekran funnel"): how many visits reached
/// each step.
/// </summary>
public sealed record PilotFunnel(int Selected, int Previewed, int Options, int PaymentScreen, int Paid, int Printing, int Printed)
{
    public static readonly PilotFunnel Empty = new(0, 0, 0, 0, 0, 0, 0);

    public PilotFunnel Plus(PilotFunnel other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new PilotFunnel(
            Selected + other.Selected,
            Previewed + other.Previewed,
            Options + other.Options,
            PaymentScreen + other.PaymentScreen,
            Paid + other.Paid,
            Printing + other.Printing,
            Printed + other.Printed);
    }
}

/// <summary>
/// One kiosk's figures for one Baku day, for the pilot report (plan section
/// 2.17, <c>AnalyticsRollupJob</c>). The app's own uploads, which belong to
/// no kiosk, have a day of their own with no <see cref="KioskId"/>.
/// </summary>
/// <remarks>
/// <para>
/// Worked out again from the records each time, never added to: the same day
/// computed twice is the same document, under the same <see cref="Id"/>, so a
/// job that runs again - or a day recomputed a week later - does not count
/// anything twice.
/// </para>
/// <para>
/// Only counts and kinds: no file name, no account, no session id.
/// </para>
/// </remarks>
public sealed class PilotDay
{
    /// <summary>The key for the app's uploads, which have no kiosk.</summary>
    public const string AppKey = "app";

    /// <summary><c>yyyy-MM-dd:{kiosk id}</c> or <c>yyyy-MM-dd:app</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The Baku date, <c>yyyy-MM-dd</c>: sorts and compares as text.</summary>
    public string Day { get; set; } = string.Empty;

    public Guid? KioskId { get; set; }

    public DateTime ComputedAt { get; set; }

    /// <summary>Customers who began at the screen (a flash drive, a cancellation, their QR).</summary>
    public int Visits { get; set; }

    public PilotFunnel Funnel { get; set; } = PilotFunnel.Empty;

    /// <summary>The screen each unfinished visit ended on.</summary>
    public List<NamedCount> Abandoned { get; set; } = [];

    public List<FormatCount> Formats { get; set; } = [];

    /// <summary>Why a chosen file would not open (the kiosk's message key).</summary>
    public List<NamedCount> ConversionErrors { get; set; } = [];

    public int PaymentsStarted { get; set; }

    public int PaymentsSucceeded { get; set; }

    /// <summary>Payments that did not go through, by reason: the bank's answer, or the time running out.</summary>
    public List<NamedCount> PaymentFailures { get; set; } = [];

    public List<MethodCount> Methods { get; set; } = [];

    /// <summary>The money part of print payments made that day (as the revenue report counts it).</summary>
    public decimal Sales { get; set; }

    public decimal Refunded { get; set; }

    public int Refunds { get; set; }

    public List<NamedCount> RefundReasons { get; set; } = [];

    public int Orders { get; set; }

    public int PrintsSucceeded { get; set; }

    public int PrintsFailed { get; set; }

    /// <summary>Why a print stopped (the printer's error key).</summary>
    public List<NamedCount> PrintErrors { get; set; } = [];

    /// <summary>The seconds the measured prints took, together; with <see cref="PrintsTimed"/>, the average.</summary>
    public long PrintSeconds { get; set; }

    public int PrintsTimed { get; set; }

    public int Cancelled { get; set; }

    /// <summary>Sheets used by orders, by paper size (A4, A3).</summary>
    public List<NamedCount> Paper { get; set; } = [];

    public int TestSheets { get; set; }

    /// <summary>How many percentage points each of the printer's supplies went down.</summary>
    public List<NamedCount> Toner { get; set; } = [];

    public int TonerReplaced { get; set; }

    /// <summary>Incidents opened that day, by severity.</summary>
    public List<NamedCount> Incidents { get; set; } = [];

    public static string IdOf(string day, Guid? kioskId) => $"{day}:{(kioskId is { } id ? id.ToString("N") : AppKey)}";
}
