using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Kiosks;

/// <summary><c>operator_kiosk_logins</c>: an operator at a kiosk, by the QR it showed (T-392).</summary>
public sealed class OperatorKioskLoginRepository : IOperatorKioskLoginRepository
{
    private readonly MongoContext _context;

    public OperatorKioskLoginRepository(MongoContext context) => _context = context;

    public Task AddAsync(OperatorKioskLogin login, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.OperatorKioskLogins, login, cancellationToken);

    public Task<OperatorKioskLogin?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.OperatorKioskLogins.Find(l => l.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<OperatorKioskLogin?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
        => _context.OperatorKioskLogins.Find(l => l.Code == code).FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(OperatorKioskLogin login, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.OperatorKioskLogins, login, cancellationToken);
}
