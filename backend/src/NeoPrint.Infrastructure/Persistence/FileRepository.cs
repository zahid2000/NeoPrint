using MongoDB.Driver;

using NeoPrint.Application.Files;
using NeoPrint.Domain.Files;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IFileRepository" />
/// <remarks>
/// Deleted files are filtered out here rather than removed from the
/// collection: the row is what a job, a receipt and an audit entry point at,
/// and dropping it would leave them referring to nothing. The bytes are gone
/// from storage either way, which is what the retention rules are about
/// (plan section 2.13).
/// </remarks>
public sealed class FileRepository : IFileRepository
{
    private readonly MongoContext _context;

    public FileRepository(MongoContext context) => _context = context;

    public Task<StoredFile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Files.Find(file => file.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<StoredFile>> ListAsync(
        Guid userId, int skip, int take, CancellationToken cancellationToken = default)
        => await _context.Files
            .Find(file => file.UserId == userId && file.DeletedAt == null)
            .SortByDescending(file => file.CreatedAt)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<long> CountAsync(Guid userId, CancellationToken cancellationToken = default)
        => _context.Files.CountDocumentsAsync(
            file => file.UserId == userId && file.DeletedAt == null, cancellationToken: cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Sums the plaintext lengths, which is what the user recognises as the
    /// size of their files - the encrypted objects are slightly larger, and
    /// charging somebody for the encryption overhead would be hard to explain.
    /// </remarks>
    public async Task<long> TotalBytesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var files = await _context.Files
            .Find(file => file.UserId == userId && file.DeletedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return files.Sum(file =>
            file.Original.PlaintextLength
            + (file.Pdf is not null && file.Pdf.Key != file.Original.Key ? file.Pdf.PlaintextLength : 0)
            + file.Versions.Sum(version => version.Object.PlaintextLength));
    }

    // Through AggregateWriter like every other aggregate: the status changes
    // the file records (upload, conversion, edit) reach state_transitions, and
    // two writers of the same file cannot silently overwrite each other - the
    // second gets a ConcurrencyException. Before this the transitions were
    // dropped and the dashboard had no conversion failures to count
    // (docs/DECISIONS.md).
    public Task AddAsync(StoredFile file, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.Files, file, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListLiveIdsAsync(Guid userId, CancellationToken cancellationToken = default)
        => await _context.Files
            .Find(f => f.UserId == userId && f.DeletedAt == null)
            .Project(f => f.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Guid>> ListAutoDeleteDueAsync(DateTime now, int take, CancellationToken cancellationToken = default)
    {
        var filter = Builders<StoredFile>.Filter;
        var due = filter.Eq(f => f.DeletedAt, null) & (
            (filter.Eq(f => f.AutoDelete, AutoDeleteRule.AfterPrint) & filter.Ne(f => f.PrintedAt, null))
            | (filter.Eq(f => f.AutoDelete, AutoDeleteRule.AfterOneDay) & filter.Lte(f => f.CreatedAt, now.AddDays(-1)))
            | (filter.Eq(f => f.AutoDelete, AutoDeleteRule.AfterSevenDays) & filter.Lte(f => f.CreatedAt, now.AddDays(-7)))
            | (filter.Eq(f => f.AutoDelete, AutoDeleteRule.AfterThirtyDays) & filter.Lte(f => f.CreatedAt, now.AddDays(-30))));

        return await _context.Files
            .Find(due)
            .SortBy(f => f.CreatedAt)
            .Limit(take)
            .Project(f => f.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task UpdateAsync(StoredFile file, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Files, file, cancellationToken);
}
