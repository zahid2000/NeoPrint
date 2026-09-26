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

    public Task AddAsync(StoredFile file, CancellationToken cancellationToken = default)
        => _context.Files.InsertOneAsync(file, cancellationToken: cancellationToken);

    public Task UpdateAsync(StoredFile file, CancellationToken cancellationToken = default)
        => _context.Files.ReplaceOneAsync(
            existing => existing.Id == file.Id, file, cancellationToken: cancellationToken);
}
