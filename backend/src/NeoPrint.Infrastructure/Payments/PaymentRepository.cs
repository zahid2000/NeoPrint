using MongoDB.Driver;

using NeoPrint.Application.Payments;
using NeoPrint.Domain.Payments;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Payments;

/// <inheritdoc cref="IPaymentRepository" />
public sealed class PaymentRepository : IPaymentRepository
{
    private static readonly PaymentStatus[] Unsettled = [PaymentStatus.Pending, PaymentStatus.Expired];

    private readonly MongoContext _context;

    public PaymentRepository(MongoContext context) => _context = context;

    public Task<Payment?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Payments.Find(payment => payment.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<Payment?> FindByProviderRefAsync(string provider, string providerRef, CancellationToken cancellationToken = default)
        => _context.Payments
            .Find(payment => payment.Provider == provider && payment.ProviderRef == providerRef)
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<Payment>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => await _context.Payments
            .Find(payment => payment.JobId == jobId)
            .SortByDescending(payment => payment.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<Payment> Items, long Total)> SearchAsync(
        PaymentFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var builder = Builders<Payment>.Filter;
        var where = builder.Empty;

        if (filter.KioskIds is { } kiosks)
        {
            where &= builder.In(payment => payment.KioskId, kiosks.Select(id => (Guid?)id));
        }

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            where &= builder.In(payment => payment.Status, statuses);
        }

        if (filter.Methods is { Count: > 0 } methods)
        {
            where &= builder.In(payment => payment.Method, methods);
        }

        if (filter.Channel is { } channel)
        {
            where &= builder.Eq(payment => payment.Channel, channel);
        }

        if (filter.Purpose is { } purpose)
        {
            where &= builder.Eq(payment => payment.Purpose, purpose);
        }

        if (filter.From is { } from)
        {
            where &= builder.Gte(payment => payment.CreatedAt, from);
        }

        if (filter.To is { } to)
        {
            where &= builder.Lt(payment => payment.CreatedAt, to);
        }

        if (filter.HadError == true)
        {
            where &= builder.Or(
                builder.Ne(payment => payment.FailureReason, null),
                builder.In(payment => payment.Status, [PaymentStatus.Failed, PaymentStatus.RefundFailed]));
        }

        if (filter.PaymentIds is { } ids)
        {
            where &= builder.In(payment => payment.Id, ids);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            var any = builder.Eq(payment => payment.ProviderRef, search);

            if (Guid.TryParse(search, out var id))
            {
                any |= builder.Eq(payment => payment.Id, id);
            }

            if (filter.JobId is { } jobId)
            {
                any |= builder.Eq(payment => payment.JobId, jobId);
            }

            where &= any;
        }

        var total = await _context.Payments.CountDocumentsAsync(where, cancellationToken).ConfigureAwait(false);
        var items = await _context.Payments.Find(where)
            .SortByDescending(payment => payment.CreatedAt)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, total);
    }

    public async Task<IReadOnlyList<Payment>> ListPaidAsync(
        DateTime from, DateTime to, IReadOnlyCollection<Guid>? kioskIds, CancellationToken cancellationToken = default)
    {
        var builder = Builders<Payment>.Filter;
        var where = builder.Gte(payment => payment.PaidAt, from) & builder.Lt(payment => payment.PaidAt, to);

        if (kioskIds is { } kiosks)
        {
            where &= builder.In(payment => payment.KioskId, kiosks.Select(id => (Guid?)id));
        }

        return await _context.Payments.Find(where).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task AddAsync(Payment payment, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.Payments, payment, cancellationToken);

    public Task UpdateAsync(Payment payment, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Payments, payment, cancellationToken);

    public async Task<IReadOnlyList<Payment>> ListOverdueAsync(DateTime now, int take, CancellationToken cancellationToken = default)
        => await _context.Payments
            .Find(payment => payment.Status == PaymentStatus.Pending && payment.ExpiresAt <= now)
            .SortBy(payment => payment.ExpiresAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Payment>> ListToReconcileAsync(
        DateTime silentBefore, DateTime notBefore, int take, CancellationToken cancellationToken = default)
        => await _context.Payments
            .Find(payment => Unsettled.Contains(payment.Status)
                             && payment.ReconciledAt == null
                             && payment.Provider != null
                             && payment.CreatedAt <= silentBefore
                             && payment.CreatedAt >= notBefore)
            .SortBy(payment => payment.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
