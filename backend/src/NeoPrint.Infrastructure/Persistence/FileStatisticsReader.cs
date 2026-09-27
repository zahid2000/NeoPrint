using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Application.Dashboard;
using NeoPrint.Domain.Files;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IFileStatistics" />
/// <remarks>
/// Counted in the database rather than by loading files: the dashboard asks on
/// every visit, and the collection grows with every upload. Failures are
/// counted from <c>state_transitions</c>, which records when each file entered
/// <c>Failed</c> - the file itself only knows that it is failed now.
/// </remarks>
public sealed class FileStatisticsReader : IFileStatistics
{
    private readonly MongoContext _context;

    public FileStatisticsReader(MongoContext context) => _context = context;

    public async Task<FileStatistics> GetAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var totals = await _context.Files
            .Aggregate()
            .Match(file => file.DeletedAt == null)
            .Group(new BsonDocument
            {
                { "_id", BsonNull.Value },
                { "count", new BsonDocument("$sum", 1) },
                { "bytes", new BsonDocument("$sum", "$Original.PlaintextLength") },
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var inProgress = await _context.Files
            .CountDocumentsAsync(
                file => file.DeletedAt == null
                        && (file.Status == FileStatus.Pending || file.Status == FileStatus.Converting),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var failed = nameof(FileStatus.Failed);
        var entity = nameof(StoredFile);
        var dayAgo = now.AddHours(-24);
        var weekAgo = now.AddDays(-7);

        var failedLastWeek = await _context.StateTransitions
            .Find(transition => transition.EntityType == entity
                                && transition.ToStatus == failed
                                && transition.CreatedAt >= weekAgo)
            .Project(transition => transition.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new FileStatistics(
            totals?["count"].ToInt64() ?? 0,
            totals?["bytes"].ToInt64() ?? 0,
            inProgress,
            failedLastWeek.Count(at => at >= dayAgo),
            failedLastWeek.Count);
    }
}
