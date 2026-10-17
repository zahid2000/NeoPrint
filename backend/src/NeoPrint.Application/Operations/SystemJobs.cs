using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Analytics;
using NeoPrint.Domain.Operations;

namespace NeoPrint.Application.Operations;

/// <summary>"Arxa plan işləri" (T-215): the last run of each check, and the kiosk events that were not kept.</summary>
/// <remarks>
/// For an account that sees every kiosk: the queues, the ledger and the
/// files belong to the whole system, not to a kiosk.
/// </remarks>
public sealed record GetSystemJobsQuery(Guid AdminUserId) : IRequest<SystemJobsView>;

/// <param name="Label">How the panel names what a finding is about: an account's PublicId; null otherwise.</param>
public sealed record JobFindingView(string Kind, string Ref, decimal? Amount, decimal? Other, string? Label);

public sealed record JobRunView(
    DateTime StartedAt,
    DateTime FinishedAt,
    bool DryRun,
    bool Succeeded,
    string? Error,
    IReadOnlyDictionary<string, long> Counts,
    IReadOnlyList<JobFindingView> Findings,
    IReadOnlyList<DeadLetterQueue>? Queues);

public sealed record RejectedEventsView(Guid KioskId, string? KioskCode, int Today, int Week, string? LastUnknownName);

/// <param name="Today">Kiosk events not kept today (Baku), all kiosks.</param>
public sealed record AnalyticsRejectionsView(int Today, int Week, IReadOnlyList<RejectedEventsView> Kiosks);

/// <param name="Runs">By job name (<see cref="JobNames"/>); a job that never ran is missing.</param>
public sealed record SystemJobsView(IReadOnlyDictionary<string, JobRunView> Runs, AnalyticsRejectionsView Analytics);

public sealed class GetSystemJobsHandler : IRequestHandler<GetSystemJobsQuery, SystemJobsView>
{
    private readonly IPermissionResolver _permissions;
    private readonly IJobRunStore _runs;
    private readonly IAnalyticsRejectionStore _rejections;
    private readonly IKioskRepository _kiosks;
    private readonly IUserRepository _users;
    private readonly IClock _clock;

    public GetSystemJobsHandler(
        IPermissionResolver permissions, IJobRunStore runs, IAnalyticsRejectionStore rejections, IKioskRepository kiosks, IUserRepository users, IClock clock)
    {
        _permissions = permissions;
        _runs = runs;
        _rejections = rejections;
        _kiosks = kiosks;
        _users = users;
        _clock = clock;
    }

    public async Task<SystemJobsView> Handle(GetSystemJobsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);

        if (!effective.AllKiosks)
        {
            throw new UnauthorizedAccessException("The background jobs concern every kiosk.");
        }

        var latest = await _runs
            .LatestAsync([JobNames.FileCleanup, JobNames.DeadLetters, JobNames.WalletLedger, JobNames.PaymentConsistency], cancellationToken)
            .ConfigureAwait(false);
        var runs = new Dictionary<string, JobRunView>(StringComparer.Ordinal);

        foreach (var (name, run) in latest)
        {
            var findings = new List<JobFindingView>();

            foreach (var finding in run.Findings)
            {
                string? label = null;

                if (finding.Kind == "walletMismatch" && Guid.TryParse(finding.Ref, out var userId))
                {
                    label = (await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false))?.PublicId;
                }

                findings.Add(new JobFindingView(finding.Kind, finding.Ref, finding.Amount, finding.Other, label));
            }

            runs[name] = new JobRunView(run.StartedAt, run.FinishedAt, run.DryRun, run.Succeeded, run.Error, run.Counts, findings, run.Queues);
        }

        var now = _clock.UtcNow;
        var today = PilotDayBuilder.DayOf(now);
        var week = PilotDayBuilder.DayOf(now.AddDays(-6));
        var rows = await _rejections.ListAsync(week, cancellationToken).ConfigureAwait(false);
        var codes = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(kiosk => kiosk.Id, kiosk => kiosk.Code);

        var kiosks = rows
            .GroupBy(row => row.KioskId)
            .Select(group => new RejectedEventsView(
                group.Key,
                codes.GetValueOrDefault(group.Key),
                group.Where(row => row.Day == today).Sum(row => row.Total),
                group.Sum(row => row.Total),
                group.OrderByDescending(row => row.LastAt).Select(row => row.LastUnknownName).FirstOrDefault(name => name is not null)))
            .OrderByDescending(kiosk => kiosk.Today)
            .ThenByDescending(kiosk => kiosk.Week)
            .ToList();

        return new SystemJobsView(runs, new AnalyticsRejectionsView(kiosks.Sum(k => k.Today), kiosks.Sum(k => k.Week), kiosks));
    }
}
