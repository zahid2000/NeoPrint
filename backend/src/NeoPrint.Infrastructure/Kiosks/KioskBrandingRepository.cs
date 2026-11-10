using MongoDB.Driver;

using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Kiosks;

/// <summary><c>kiosk_branding</c>: the default's document and one per kiosk with its own words (T-387).</summary>
public sealed class KioskBrandingRepository : IKioskBrandingRepository
{
    private readonly MongoContext _context;

    public KioskBrandingRepository(MongoContext context) => _context = context;

    public Task<KioskBranding?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.KioskBrandings.Find(b => b.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public async Task SaveAsync(KioskBranding branding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branding);

        var stored = await _context.KioskBrandings.CountDocumentsAsync(b => b.Id == branding.Id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (stored == 0)
        {
            await AggregateWriter.InsertAsync(_context, _context.KioskBrandings, branding, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AggregateWriter.ReplaceAsync(_context, _context.KioskBrandings, branding, cancellationToken).ConfigureAwait(false);
        }
    }
}
