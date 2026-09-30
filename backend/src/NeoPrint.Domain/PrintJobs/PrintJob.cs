using NeoPrint.Documents;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Pricing;

namespace NeoPrint.Domain.PrintJobs;

/// <summary>
/// Where a print job is in its life (plan section 5; docs/STATE_MACHINES.md,
/// "PrintJob").
/// </summary>
/// <remarks>
/// There is no RefundPending or Refunded here on purpose: a refund is an
/// entity of its own and the job's money story is told by the payment
/// (plan section 2.5).
/// </remarks>
public enum PrintJobStatus
{
    Draft = 0,
    PendingPayment = 1,
    Paid = 2,
    Queued = 3,
    Printing = 4,
    Completed = 5,
    Expired = 6,
    Cancelled = 7,

    /// <summary>Printing stopped and the kiosk knows it did (scenario A).</summary>
    Interrupted = 8,

    /// <summary>
    /// The kiosk went quiet and nobody knows whether it printed (scenario B).
    /// Left only with new information - never by a timer, never with an
    /// automatic refund (docs/FAILURE_RECOVERY.md).
    /// </summary>
    Suspected = 9,
}

/// <summary>Where the file came from (plan section 5, <c>Source</c>).</summary>
public enum PrintJobSource
{
    /// <summary>A USB flash drive at the kiosk; the file never leaves the kiosk.</summary>
    Flash = 0,
    MobileSession = 1,
    UserQr = 2,
    Scheduled = 3,
}

/// <summary>
/// A print order (plan section 5, <c>print_jobs</c>).
/// </summary>
/// <remarks>
/// <para>
/// The price is a <see cref="PriceSnapshot"/> taken the moment the job is made
/// from the kiosk's profile: editing or deleting the profile afterwards cannot
/// change what this job costs (plan section 2.3, docs/TASKS.md T-047).
/// </para>
/// <para>
/// A flash job has no file on the server - the bytes stay in the kiosk's
/// encrypted spool (plan section 7.5) - so it carries the file's name and page
/// count as the kiosk reported them, and the kiosk prints from its own copy.
/// </para>
/// </remarks>
public sealed class PrintJob : Entity
{
    /// <summary>
    /// Six digits, printed on the receipt and typed on the cancel screen
    /// (plan section 7.3, screen 11). Unique across all jobs.
    /// </summary>
    public string JobCode { get; set; } = string.Empty;

    public Guid KioskId { get; set; }

    /// <summary>Null for an anonymous flash job.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The server-side file, for a job made from the mobile app.</summary>
    public Guid? FileId { get; set; }

    /// <summary>The file's name on the flash drive, for the queue panel and the history.</summary>
    public string? LocalFileName { get; set; }

    public PrintJobSource Source { get; set; }

    /// <summary>Pages in the document; the range picks from these.</summary>
    public int PageCount { get; set; }

    public PrintOptions Options { get; set; } = PrintOptions.Default;

    /// <summary>The price as it was when the job was made; never recomputed.</summary>
    public PriceSnapshot Price { get; set; } = null!;

    /// <summary>Sheets of paper the job takes, for the paper check and the counter (plan section 2.15).</summary>
    public int RequiredSheets { get; set; }

    /// <summary>Changed only through the transition methods below.</summary>
    public PrintJobStatus Status { get; private set; } = PrintJobStatus.Draft;

    /// <summary>The payment that paid for it (step 4).</summary>
    public Guid? PaymentId { get; private set; }

    /// <summary>
    /// Set when a Development build queued the job without a payment (plan
    /// step 3: "Development-də dev bayrağı ilə job birbaşa Queued"). Never set
    /// outside Development; kept so such a job is recognisable in the history.
    /// </summary>
    public bool PaymentSkipped { get; private set; }

    /// <summary>Sides the printer reported as printed.</summary>
    public int PrintedSides { get; private set; }

    /// <summary>Sheets the printer reported as used; what the paper counter goes down by.</summary>
    public int PrintedSheets { get; private set; }

    /// <summary>Why printing stopped, as a stored message (<see cref="UserMessage.Encode"/>).</summary>
    public string? ErrorReason { get; private set; }

    /// <summary>Why the job was cancelled, as a stored message.</summary>
    public string? CancelReason { get; private set; }

    public DateTime? QueuedAt { get; private set; }

    public DateTime? StartedAt { get; private set; }

    public DateTime? FinishedAt { get; private set; }

    /// <summary>How many times printing was started; above one means it was put back in the queue.</summary>
    public int Attempts { get; private set; }

    /// <summary>Jobs that are still in front of the printer: shown in the queue.</summary>
    public bool IsInQueue => Status is PrintJobStatus.Queued or PrintJobStatus.Printing;

    /// <summary>Jobs that have finished one way or another.</summary>
    public bool IsFinished => Status is PrintJobStatus.Completed or PrintJobStatus.Cancelled or PrintJobStatus.Expired;

    /// <summary>The transitions a job may make (docs/STATE_MACHINES.md, "PrintJob").</summary>
    public static readonly StateMachine<PrintJobStatus> Transitions =
        new StateMachine<PrintJobStatus>(nameof(PrintJob))
            .Allow(PrintJobStatus.Draft, PrintJobStatus.PendingPayment, PrintJobStatus.Cancelled, PrintJobStatus.Expired)
            .Allow(PrintJobStatus.PendingPayment, PrintJobStatus.Paid, PrintJobStatus.Expired, PrintJobStatus.Cancelled)
            .Allow(PrintJobStatus.Paid, PrintJobStatus.Queued)
            .Allow(PrintJobStatus.Queued, PrintJobStatus.Printing, PrintJobStatus.Cancelled, PrintJobStatus.Suspected)
            .Allow(
                PrintJobStatus.Printing,
                PrintJobStatus.Completed,
                PrintJobStatus.Interrupted,
                PrintJobStatus.Suspected)
            .Allow(PrintJobStatus.Interrupted, PrintJobStatus.Queued, PrintJobStatus.Cancelled)
            .Allow(
                PrintJobStatus.Suspected,
                PrintJobStatus.Completed,
                PrintJobStatus.Interrupted,
                PrintJobStatus.Cancelled,
                PrintJobStatus.Queued);

    /// <summary>A new job, priced and in <see cref="PrintJobStatus.Draft"/>.</summary>
    public static PrintJob Create(
        string jobCode,
        Guid kioskId,
        PrintJobSource source,
        int pageCount,
        PrintOptions options,
        PriceSnapshot price,
        DateTime now,
        Guid? userId = null,
        Guid? fileId = null,
        string? localFileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobCode);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(price);

        return new PrintJob
        {
            JobCode = jobCode,
            KioskId = kioskId,
            Source = source,
            PageCount = pageCount,
            Options = options,
            Price = price,
            RequiredSheets = PhysicalSheets(options, price.Price.Pages),
            UserId = userId,
            FileId = fileId,
            LocalFileName = localFileName,
            CreatedAt = now,
        };
    }

    /// <summary>Sheets the printer actually uses (<see cref="PrintSheets.Physical"/>).</summary>
    public static int PhysicalSheets(PrintOptions options, int selectedPages)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PrintSheets.Physical(selectedPages, options.PagesPerSheet, options.Copies, options.Duplex);
    }

    /// <summary>The order is placed and waits for its payment (plan section 7.3, screens 6–8).</summary>
    public void AwaitPayment(ActorContext actor, DateTime now)
        => Move(PrintJobStatus.PendingPayment, actor, now);

    /// <summary>
    /// The mobile app user who paid for it (plan section 9.2, step 4): the job,
    /// its receipt and - for a flash file - the file join their account.
    /// </summary>
    /// <exception cref="DomainException">It already belongs to somebody else.</exception>
    public void AssignOwner(Guid userId)
    {
        if (UserId is { } owner && owner != userId)
        {
            throw new DomainException("printJobs.belongsToAnotherUser");
        }

        UserId = userId;
    }

    /// <summary>The payment went through.</summary>
    public void MarkPaid(Guid? paymentId, ActorContext actor, DateTime now)
    {
        Move(PrintJobStatus.Paid, actor, now);
        PaymentId ??= paymentId;
    }

    /// <summary>
    /// Development only: paid without a payment, so the print flow can be
    /// exercised before payments exist (plan step 3). The caller decides it is
    /// Development; the job only remembers that it happened.
    /// </summary>
    public void SkipPaymentForDevelopment(ActorContext actor, DateTime now)
    {
        if (Status == PrintJobStatus.Draft)
        {
            AwaitPayment(actor, now);
        }

        Move(PrintJobStatus.Paid, actor, now);
        PaymentSkipped = true;
    }

    /// <summary>Into the kiosk's queue, first time or again after an interruption.</summary>
    public void Enqueue(ActorContext actor, DateTime now)
    {
        var before = Status;
        Move(PrintJobStatus.Queued, actor, now);

        if (before != PrintJobStatus.Queued)
        {
            QueuedAt = now;
            ErrorReason = null;
        }
    }

    /// <summary>The kiosk has started sending it to the printer.</summary>
    public void StartPrinting(ActorContext actor, DateTime now)
    {
        var before = Status;
        Move(PrintJobStatus.Printing, actor, now);

        if (before != PrintJobStatus.Printing)
        {
            StartedAt = now;
            Attempts++;
        }
    }

    /// <summary>Printed. The counts are what the printer reported.</summary>
    /// <exception cref="DomainException">A count is negative or larger than the job.</exception>
    public void Complete(int printedSides, int printedSheets, ActorContext actor, DateTime now)
    {
        var before = Status;
        Move(PrintJobStatus.Completed, actor, now);

        if (before == PrintJobStatus.Completed)
        {
            // The same report arriving twice: the first one counted.
            return;
        }

        RecordCounts(printedSides, printedSheets);
        FinishedAt = now;
    }

    /// <summary>Printing stopped and the kiosk knows it; the reason is for the history and the panel.</summary>
    public void Interrupt(UserMessage reason, int printedSides, int printedSheets, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var before = Status;
        Move(PrintJobStatus.Interrupted, actor, now);

        if (before == PrintJobStatus.Interrupted)
        {
            return;
        }

        RecordCounts(printedSides, printedSheets);
        ErrorReason = reason.Encode();
    }

    /// <summary>Taken out of the queue by the customer, an admin or the system.</summary>
    public void Cancel(UserMessage reason, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var before = Status;
        Move(PrintJobStatus.Cancelled, actor, now);

        if (before != PrintJobStatus.Cancelled)
        {
            CancelReason = reason.Encode();
            FinishedAt = now;
        }
    }

    /// <summary>Nobody paid in time.</summary>
    public void Expire(ActorContext actor, DateTime now)
    {
        var before = Status;
        Move(PrintJobStatus.Expired, actor, now);

        if (before != PrintJobStatus.Expired)
        {
            FinishedAt = now;
        }
    }

    /// <summary>The kiosk went quiet with this job in hand; its outcome is unknown.</summary>
    public void MarkSuspected(ActorContext actor, DateTime now) => Move(PrintJobStatus.Suspected, actor, now);

    private void RecordCounts(int printedSides, int printedSheets)
    {
        if (printedSides < 0 || printedSheets < 0 || printedSheets > printedSides)
        {
            throw new DomainException("printJobs.countsInvalid");
        }

        // A report cannot claim more than the job asked for; a printer that
        // says otherwise is wrong, and the paper counter must not follow it.
        PrintedSides = Math.Min(printedSides, Price.Price.Sides);
        PrintedSheets = Math.Min(printedSheets, RequiredSheets);
    }

    private void Move(PrintJobStatus next, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Record(Transitions.Transition(Id, Status, next, actor, now));
        Status = next;
    }
}
