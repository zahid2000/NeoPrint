using MongoDB.Driver;

using NeoPrint.Application.Support;
using NeoPrint.Domain.Support;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Support;

/// <summary><c>contact_settings</c>: the app's "Əlaqə" channels, one document (T-438).</summary>
public sealed class ContactSettingsRepository : IContactSettingsRepository
{
    private readonly MongoContext _context;

    public ContactSettingsRepository(MongoContext context) => _context = context;

    public Task<ContactSettings?> FindAsync(CancellationToken cancellationToken = default)
        => _context.ContactSettings.Find(s => s.Id == ContactSettings.DefaultId).FirstOrDefaultAsync(cancellationToken)!;

    public async Task SaveAsync(ContactSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var stored = await _context.ContactSettings.CountDocumentsAsync(s => s.Id == settings.Id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (stored == 0)
        {
            await AggregateWriter.InsertAsync(_context, _context.ContactSettings, settings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await AggregateWriter.ReplaceAsync(_context, _context.ContactSettings, settings, cancellationToken).ConfigureAwait(false);
        }
    }
}
