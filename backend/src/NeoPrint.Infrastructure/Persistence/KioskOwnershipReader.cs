using MongoDB.Driver;

using NeoPrint.Application.Admin;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IKioskOwnershipReader" />
public sealed class KioskOwnershipReader : IKioskOwnershipReader
{
    private readonly MongoContext _context;

    public KioskOwnershipReader(MongoContext context) => _context = context;

    public async Task<KioskOwner> KioskOfAsync(RecordKind kind, Guid id, CancellationToken cancellationToken = default)
    {
        switch (kind)
        {
            case RecordKind.Payment:
                return await PaymentAsync(id, cancellationToken).ConfigureAwait(false);

            case RecordKind.Refund:
                var paymentId = await _context.Refunds.Find(refund => refund.Id == id)
                    .Project(refund => (Guid?)refund.PaymentId)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                return paymentId is { } payment ? await PaymentAsync(payment, cancellationToken).ConfigureAwait(false) : new KioskOwner(false, null);

            case RecordKind.FiscalReceipt:
                return Owner(await _context.FiscalReceipts.Find(receipt => receipt.Id == id)
                    .Project(receipt => new Holder { KioskId = receipt.KioskId })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false));

            case RecordKind.Receipt:
                return Owner(await _context.Receipts.Find(receipt => receipt.Id == id)
                    .Project(receipt => new Holder { KioskId = receipt.KioskId })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false));

            case RecordKind.SupportClaim:
                return Owner(await _context.SupportClaims.Find(claim => claim.Id == id)
                    .Project(claim => new Holder { KioskId = claim.KioskId })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false));

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private async Task<KioskOwner> PaymentAsync(Guid id, CancellationToken cancellationToken)
        => Owner(await _context.Payments.Find(payment => payment.Id == id)
            .Project(payment => new Holder { KioskId = payment.KioskId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false));

    private static KioskOwner Owner(Holder? holder) => holder is null ? new KioskOwner(false, null) : new KioskOwner(true, holder.KioskId);

    private sealed class Holder
    {
        public Guid? KioskId { get; init; }
    }
}
