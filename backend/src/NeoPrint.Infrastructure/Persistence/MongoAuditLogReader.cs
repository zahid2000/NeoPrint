using System.Text.RegularExpressions;

using MongoDB.Bson;
using MongoDB.Driver;

using NeoPrint.Application.Admin;
using NeoPrint.Domain.Audit;
using NeoPrint.Domain.Common;

namespace NeoPrint.Infrastructure.Persistence;

/// <summary>
/// Reads <c>audit_logs</c> for the panel's "Audit log" page, newest first, on
/// the indexes <see cref="MongoContext"/> creates (created, admin, entity,
/// correlation).
/// </summary>
public sealed class MongoAuditLogReader : IAuditLogReader
{
    private readonly MongoContext _context;

    public MongoAuditLogReader(MongoContext context) => _context = context;

    public async Task<(IReadOnlyList<AuditLog> Items, long Total)> ListAsync(
        AuditLogFilter filter,
        IReadOnlyCollection<string>? kioskIds,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var builder = Builders<AuditLog>.Filter;
        var query = builder.Empty;

        if (kioskIds is not null)
        {
            query &= builder.Eq(entry => entry.EntityType, AuditLogQueryHandlers.KioskEntity)
                     & builder.In(entry => entry.EntityId, kioskIds);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            // A prefix: "kiosks." finds every kiosk action. Escaped - the text is the reader's.
            query &= builder.Regex(
                entry => entry.Action,
                new BsonRegularExpression("^" + Regex.Escape(filter.Action.Trim())));
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            query &= builder.Eq(entry => entry.EntityType, filter.EntityType.Trim());
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
        {
            query &= builder.Eq(entry => entry.EntityId, filter.EntityId.Trim());
        }

        if (filter.AdminUserId is { } admin)
        {
            query &= builder.Eq(entry => entry.AdminUserId, admin);
        }

        if (Enum.TryParse<ActorType>(filter.ActorType, ignoreCase: true, out var actor))
        {
            query &= builder.Eq(entry => entry.ActorType, actor);
        }

        if (filter.From is { } from)
        {
            query &= builder.Gte(entry => entry.CreatedAt, from);
        }

        if (filter.To is { } to)
        {
            query &= builder.Lte(entry => entry.CreatedAt, to);
        }

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            query &= builder.Eq(entry => entry.CorrelationId, filter.CorrelationId.Trim());
        }

        var items = await _context.AuditLogs
            .Find(query)
            .SortByDescending(entry => entry.CreatedAt)
            .Skip(skip)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var total = await _context.AuditLogs.CountDocumentsAsync(query, cancellationToken).ConfigureAwait(false);

        return (items, total);
    }
}
