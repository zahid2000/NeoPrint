namespace NeoPrint.Kiosk.Core.Printing;

/// <summary>
/// How fast this kiosk's printer goes, learnt from the jobs it finished, and
/// when the jobs in the queue should be ready (T-360).
/// </summary>
/// <remarks>
/// An estimate for the screen only - nothing is charged, cancelled or
/// reported by it. Until a job has finished it assumes a small office
/// printer; every finished job moves the pace a third of the way towards
/// what that job took, so one slow job (a paper jam) does not make every
/// estimate wrong. Kept in memory: after a restart it learns again.
/// </remarks>
public sealed class PrintPace
{
    /// <summary>About 15 pages a minute, before anything was learnt.</summary>
    public const double DefaultSecondsPerSide = 4;

    /// <summary>Getting a job to the printer and the first page out.</summary>
    public const double JobOverheadSeconds = 8;

    private const double Weight = 1.0 / 3;
    private const double Fastest = 1;
    private const double Slowest = 60;

    private readonly object _gate = new();
    private double _secondsPerSide = DefaultSecondsPerSide;

    public double SecondsPerSide
    {
        get
        {
            lock (_gate)
            {
                return _secondsPerSide;
            }
        }
    }

    /// <summary>A finished job: <paramref name="sides"/> pages in <paramref name="took"/>.</summary>
    public void Learn(int sides, TimeSpan took)
    {
        if (sides <= 0 || took <= TimeSpan.Zero)
        {
            return;
        }

        var seen = Math.Clamp((took.TotalSeconds - JobOverheadSeconds) / sides, Fastest, Slowest);

        lock (_gate)
        {
            _secondsPerSide += (seen - _secondsPerSide) * Weight;
        }
    }

    /// <summary>
    /// Pages done so far by the clock, for a printer whose counter cannot be
    /// read: never the last page - that is the printer's to say.
    /// </summary>
    public int SidesByTime(int total, TimeSpan elapsed)
    {
        if (total <= 0)
        {
            return 0;
        }

        var done = (int)((elapsed.TotalSeconds - JobOverheadSeconds) / SecondsPerSide);

        return Math.Clamp(done, 0, total - 1);
    }

    /// <summary>
    /// When each job should be ready, in queue order: the one printing first
    /// with what is left of it, then each waiting one after the one before.
    /// </summary>
    public IReadOnlyList<DateTime> Finishes(IReadOnlyList<QueuedWork> jobs, DateTime now)
    {
        var pace = SecondsPerSide;
        var at = now;
        var finishes = new List<DateTime>(jobs.Count);

        foreach (var job in jobs)
        {
            var left = Math.Max(0, job.Sides - job.PrintedSides);
            var seconds = left * pace + (job.Printing ? 0 : JobOverheadSeconds);
            at = at.AddSeconds(seconds);
            finishes.Add(at);
        }

        return finishes;
    }
}

/// <summary>One job in <see cref="PrintPace.Finishes"/>.</summary>
public readonly record struct QueuedWork(int Sides, int PrintedSides, bool Printing);
