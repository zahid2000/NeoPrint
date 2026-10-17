using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Refunds;
using NeoPrint.Application.Wallets;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Operations;

namespace NeoPrint.Application.Operations;

/// <summary>
/// <c>WalletLedgerReconciliationJob</c> (plan section 6, daily; T-215): each
/// account's ledger summed against its <c>wallets.Balance</c> - the ledger is
/// the truth, the balance only its running total (CLAUDE.md).
/// </summary>
/// <remarks>
/// Finds, never fixes: a mismatch is a <c>Critical</c> incident and an audit
/// entry, and the money stays exactly as it is until a person has looked at
/// the ledger. One incident per account while it is open, so a run again adds
/// nothing.
/// </remarks>
public sealed record RunWalletLedgerCheckCommand : IRequest<JobRun>;

public sealed class RunWalletLedgerCheckHandler : IRequestHandler<RunWalletLedgerCheckCommand, JobRun>
{
    private const int Page = 200;

    private readonly IWalletRepository _wallets;
    private readonly IWalletTransactionRepository _ledger;
    private readonly IncidentReporter _incidents;
    private readonly IAuditWriter _audit;
    private readonly IJobRunStore _runs;
    private readonly IClock _clock;
    private readonly ILogger<RunWalletLedgerCheckHandler> _logger;

    public RunWalletLedgerCheckHandler(
        IWalletRepository wallets,
        IWalletTransactionRepository ledger,
        IncidentReporter incidents,
        IAuditWriter audit,
        IJobRunStore runs,
        IClock clock,
        ILogger<RunWalletLedgerCheckHandler> logger)
    {
        _wallets = wallets;
        _ledger = ledger;
        _incidents = incidents;
        _audit = audit;
        _runs = runs;
        _clock = clock;
        _logger = logger;
    }

    public static string KeyOf(Guid userId) => $"wallet-mismatch:{userId}";

    public async Task<JobRun> Handle(RunWalletLedgerCheckCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var run = new JobRun { Job = JobNames.WalletLedger, StartedAt = _clock.UtcNow };
        run.Counts["checked"] = 0;
        run.Counts["mismatches"] = 0;

        for (var skip = 0; ; skip += Page)
        {
            var wallets = await _wallets.ListAsync(skip, Page, cancellationToken).ConfigureAwait(false);

            foreach (var wallet in wallets)
            {
                run.Count("checked");
                var (amount, pages) = await _ledger.SumAsync(wallet.UserId, cancellationToken).ConfigureAwait(false);

                if (amount == wallet.Balance && pages == wallet.BonusPages)
                {
                    continue;
                }

                run.Count("mismatches");
                run.Found(new JobFinding("walletMismatch", wallet.UserId.ToString(), wallet.Balance.Amount, amount.Amount));
                NeoPrintMetrics.ReconciliationFindings.Add(
                    1, new KeyValuePair<string, object?>("check", JobNames.WalletLedger), new KeyValuePair<string, object?>("kind", "walletMismatch"));

                var raised = await _incidents
                    .RaiseAsync(
                        IncidentType.Other,
                        IncidentSeverity.Critical,
                        UserMessage.Of(
                            "incidents.walletMismatch",
                            ("balance", wallet.Balance.Amount),
                            ("ledger", amount.Amount),
                            ("pages", wallet.BonusPages),
                            ("ledgerPages", pages)),
                        KeyOf(wallet.UserId),
                        cancellationToken,
                        entityType: "Wallet",
                        entityId: wallet.UserId)
                    .ConfigureAwait(false);

                // Once per open incident: the audit says when it was found, not every day it is still there.
                if (raised is not null)
                {
                    await _audit
                        .WriteAsync(
                            new AuditEntry(
                                null,
                                "reconciliation.walletMismatch",
                                "Wallet",
                                wallet.UserId.ToString(),
                                $"balance {wallet.Balance.Amount} / {wallet.BonusPages} pages",
                                $"ledger {amount.Amount} / {pages} pages",
                                null,
                                Messages.Write("reconciliation.notCorrected"),
                                ActorType.System),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (wallets.Count < Page)
            {
                break;
            }
        }

        run.FinishedAt = _clock.UtcNow;
        await _runs.AddAsync(run, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Wallet ledger check: {Checked} accounts, {Mismatches} mismatch(es).", run.Counts["checked"], run.Counts["mismatches"]);

        return run;
    }
}
