using MongoDB.Driver;

using NeoPrint.Application.Operations;
using NeoPrint.Domain.Operations;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Wallets;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IJobRunStore" />
public sealed class MongoJobRunStore : IJobRunStore
{
    private readonly MongoContext _context;

    public MongoJobRunStore(MongoContext context) => _context = context;

    public Task AddAsync(JobRun run, CancellationToken cancellationToken = default)
        => _context.JobRuns.InsertOneAsync(run, cancellationToken);

    public async Task<IReadOnlyDictionary<string, JobRun>> LatestAsync(IReadOnlyCollection<string> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        var latest = new Dictionary<string, JobRun>(StringComparer.Ordinal);

        foreach (var job in jobs)
        {
            var run = await _context.JobRuns
                .Find(entry => entry.Job == job)
                .SortByDescending(entry => entry.FinishedAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (run is not null)
            {
                latest[job] = run;
            }
        }

        return latest;
    }
}

/// <inheritdoc cref="IFileReferenceReader" />
public sealed class MongoFileReferenceReader : IFileReferenceReader
{
    /// <summary>A job in one of these is finished; any other still prints, waits or is being looked into.</summary>
    private static readonly PrintJobStatus[] Finished = [PrintJobStatus.Completed, PrintJobStatus.Cancelled, PrintJobStatus.Expired];

    private readonly MongoContext _context;

    public MongoFileReferenceReader(MongoContext context) => _context = context;

    public async Task<FileReferences> ReadAsync(IReadOnlyCollection<Guid> files, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(keys);

        var live = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in await _context.Files.Find(f => files.Contains(f.Id) && f.DeletedAt == null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            live.Add(file.Original.Key);

            if (file.Pdf is { } pdf)
            {
                live.Add(pdf.Key);
            }

            live.UnionWith(file.Versions.Select(version => version.Object.Key));
            live.UnionWith(file.LayoutAssets.Select(asset => asset.Object.Key));
        }

        // Orders that hold a file: by the file, and by the object they print from.
        var nullableFiles = files.Select(id => (Guid?)id).ToList();
        var jobs = await _context.PrintJobs
            .Find(Builders<PrintJob>.Filter.In(job => job.FileId, nullableFiles) | Builders<PrintJob>.Filter.In("PrintFile.Key", keys))
            .Project(job => new { job.Id, job.FileId, job.Status, Key = job.PrintFile == null ? null : job.PrintFile.Key })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var jobIds = jobs.Select(job => (Guid?)job.Id).ToList();
        var refunded = (await _context.Refunds.Find(refund => jobIds.Contains(refund.JobId)).Project(refund => refund.JobId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .OfType<Guid>()
            .ToHashSet();
        var holding = jobs.Where(job => !Finished.Contains(job.Status) || refunded.Contains(job.Id)).ToList();

        var protectedFiles = holding.Select(job => job.FileId).OfType<Guid>().ToHashSet();
        var protectedKeys = holding.Select(job => job.Key).OfType<string>().ToHashSet(StringComparer.Ordinal);

        protectedFiles.UnionWith(await _context.ScheduledPrints
            .Find(schedule => files.Contains(schedule.FileId) && schedule.ClosedAt == null)
            .Project(schedule => schedule.FileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));
        protectedFiles.UnionWith(await _context.PreparedPrints
            .Find(prepared => files.Contains(prepared.FileId) && prepared.RemovedAt == null)
            .Project(prepared => prepared.FileId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        return new FileReferences(live, protectedFiles, protectedKeys);
    }
}

/// <inheritdoc cref="IMoneySnapshotReader" />
public sealed class MongoMoneySnapshotReader : IMoneySnapshotReader
{
    private readonly MongoContext _context;

    public MongoMoneySnapshotReader(MongoContext context) => _context = context;

    public async Task<MoneySnapshot> ReadAsync(DateTime since, CancellationToken cancellationToken = default)
    {
        var payments = await _context.Payments
            .Find(Builders<Payment>.Filter.Gte(p => p.PaidAt, since))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var paymentIds = payments.Select(p => p.Id).ToList();

        var jobIds = payments.Select(p => p.JobId).OfType<Guid>().ToList();
        var jobs = (await _context.PrintJobs.Find(job => jobIds.Contains(job.Id)).Project(job => new { job.Id, job.Status }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(job => job.Id, job => job.Status);

        var refunds = await _context.Refunds
            .Find(Builders<Refund>.Filter.In(r => r.PaymentId, paymentIds) | Builders<Refund>.Filter.Gte(r => r.CompletedAt, since))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var refs = paymentIds.Select(id => id.ToString()).Concat(refunds.Select(r => r.Id.ToString())).ToList();
        var ledgerRefs = (await _context.WalletTransactions
                .Find(Builders<WalletTransaction>.Filter.In(t => t.RefId, refs))
                .Project(t => t.RefId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var nullablePayments = paymentIds.Select(id => (Guid?)id).ToList();
        var fiscal = (await _context.FiscalReceipts
                .Find(receipt => nullablePayments.Contains(receipt.PaymentId))
                .Project(receipt => receipt.PaymentId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OfType<Guid>()
            .ToHashSet();

        return new MoneySnapshot(
            [
                .. payments.Where(p => p.PaidAt is not null).Select(p => new CheckedPayment(
                    p.Id,
                    p.KioskId,
                    p.Purpose,
                    p.Status,
                    p.PaidAt!.Value,
                    (p.Parts?.Money ?? p.Amount).Amount,
                    p.Parts?.Wallet.Amount ?? 0,
                    p.Parts?.BonusPages ?? 0,
                    p.JobId)),
            ],
            jobs,
            [.. refunds.Select(r => new CheckedRefund(r.Id, r.PaymentId, r.Status, r.Destination, r.Amount.Amount, r.CompletedAt))],
            ledgerRefs,
            fiscal);
    }
}
