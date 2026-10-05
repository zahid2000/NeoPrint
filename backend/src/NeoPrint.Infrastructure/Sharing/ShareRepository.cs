using MongoDB.Driver;

using NeoPrint.Application.Sharing;
using NeoPrint.Domain.Sharing;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Sharing;

/// <inheritdoc cref="IShareRepository" />
public sealed class ShareRepository : IShareRepository
{
    private readonly MongoContext _context;

    public ShareRepository(MongoContext context) => _context = context;

    public Task AddAsync(Share share, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.Shares, share, cancellationToken);

    public Task<Share?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Shares.Find(s => s.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task UpdateAsync(Share share, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Shares, share, cancellationToken);

    public async Task<IReadOnlyList<Share>> ListSentAsync(Guid userId, int take, CancellationToken cancellationToken = default)
        => await _context.Shares
            .Find(s => s.FromUserId == userId)
            .SortByDescending(s => s.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Share>> ListReceivedAsync(Guid userId, int take, CancellationToken cancellationToken = default)
        => await _context.Shares
            .Find(s => s.ToUserId == userId)
            .SortByDescending(s => s.CreatedAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<Share?> FindUnsavedAsync(Guid fileId, Guid toUserId, string pdfKey, CancellationToken cancellationToken = default)
        => _context.Shares
            .Find(s => s.FileId == fileId && s.ToUserId == toUserId && s.PdfKey == pdfKey && s.SavedAt == null)
            .SortByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)!;
}
