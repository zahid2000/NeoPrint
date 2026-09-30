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
