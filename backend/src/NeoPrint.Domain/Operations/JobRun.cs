namespace NeoPrint.Domain.Operations;

/// <summary>The background jobs whose runs are kept for the admin panel (plan section 6, T-215).</summary>
public static class JobNames
{
    public const string FileCleanup = "file-cleanup";

    public const string DeadLetters = "dead-letters";

    public const string WalletLedger = "wallet-ledger";

    public const string PaymentConsistency = "payment-consistency";
}

/// <summary>Something a run found or did: a deleted object, a mismatch.</summary>
/// <param name="Kind">What it is: <c>deleted</c>, <c>wouldDelete</c>, <c>walletMismatch</c>, <c>paidNotPrinted</c>, ...</param>
/// <param name="Ref">What it is about: a storage key, a payment, refund or account id.</param>
/// <param name="Amount">A size in bytes, or a sum of money; null when it has none.</param>
/// <param name="Other">A second figure: the ledger's sum against the balance.</param>
public sealed record JobFinding(string Kind, string Ref, decimal? Amount = null, decimal? Other = null);

/// <summary>
/// One run of a background job (T-215): when, whether it only looked
/// (<see cref="DryRun"/>), what it counted and the first of what it found -
/// what the admin panel's "Arxa plan işləri" shows. No personal data: ids,
/// storage keys and amounts.
/// </summary>
public sealed class JobRun
{
    /// <summary>The most findings one run keeps; the counts carry the rest.</summary>
    public const int MaxFindings = 50;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Job { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    public DateTime FinishedAt { get; set; }

    public bool DryRun { get; set; }

    /// <summary>False when the run stopped on an error (<see cref="Error"/>).</summary>
    public bool Succeeded { get; set; } = true;

    /// <summary>A short technical reason; never a secret or a file's content.</summary>
    public string? Error { get; set; }

    public Dictionary<string, long> Counts { get; set; } = new(StringComparer.Ordinal);

    public List<JobFinding> Findings { get; set; } = [];

    /// <summary>The dead-letter check's queues, every one of them, empty ones too; null for the other jobs.</summary>
    public List<DeadLetterQueue>? Queues { get; set; }

    public void Count(string name, long by = 1) => Counts[name] = Counts.GetValueOrDefault(name) + by;

    public void Found(JobFinding finding)
    {
        if (Findings.Count < MaxFindings)
        {
            Findings.Add(finding);
        }
    }
}

/// <summary>One dead-letter queue as last seen (plan section 6: "_error queue ... sayı admin dashboard-da").</summary>
/// <param name="OldestAt">When the oldest message failed; null when the queue is empty or the time is not known.</param>
public sealed record DeadLetterQueue(string Name, long Messages, DateTime? OldestAt);
