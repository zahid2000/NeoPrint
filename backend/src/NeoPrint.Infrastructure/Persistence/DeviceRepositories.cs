using System.Globalization;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Audit;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Infrastructure.Persistence;

/// <inheritdoc cref="IKioskDeviceRepository" />
public sealed class KioskDeviceRepository : IKioskDeviceRepository
{
    private readonly MongoContext _context;

    public KioskDeviceRepository(MongoContext context) => _context = context;

    public Task<KioskDevice?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.KioskDevices.Find(device => device.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<KioskDevice?> FindByPublicKeyAsync(string publicKey, CancellationToken cancellationToken = default)
        => _context.KioskDevices.Find(device => device.PublicKey == publicKey)
            .FirstOrDefaultAsync(cancellationToken)!;

    public Task<KioskDevice?> FindByPairingCodeHashAsync(
        string pairingCodeHash, CancellationToken cancellationToken = default)
        => _context.KioskDevices.Find(device => device.PairingCodeHash == pairingCodeHash)
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<KioskDevice>> ListAsync(
        DeviceStatus? status, CancellationToken cancellationToken = default)
    {
        var filter = status is null
            ? FilterDefinition<KioskDevice>.Empty
            : Builders<KioskDevice>.Filter.Eq(device => device.Status, status.Value);

        return await _context.KioskDevices
            .Find(filter)
            .SortByDescending(device => device.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<long> CountPendingAsync(CancellationToken cancellationToken = default)
        => _context.KioskDevices.CountDocumentsAsync(
            device => device.Status == DeviceStatus.Pending, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<KioskDevice>> ListForKioskAsync(
        Guid kioskId, CancellationToken cancellationToken = default)
        => await _context.KioskDevices
            .Find(device => device.KioskId == kioskId || device.LastKioskId == kioskId)
            .SortByDescending(device => device.ActivatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task AddAsync(KioskDevice device, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.KioskDevices, device, cancellationToken);

    public Task UpdateAsync(KioskDevice device, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.KioskDevices, device, cancellationToken);
}

/// <inheritdoc cref="IEnrollmentTokenRepository" />
public sealed class EnrollmentTokenRepository : IEnrollmentTokenRepository
{
    private readonly MongoContext _context;

    public EnrollmentTokenRepository(MongoContext context) => _context = context;

    public Task<EnrollmentToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
        => _context.EnrollmentTokens.Find(token => token.TokenHash == tokenHash)
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<EnrollmentToken>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.EnrollmentTokens
            .Find(FilterDefinition<EnrollmentToken>.Empty)
            .SortByDescending(token => token.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task AddAsync(EnrollmentToken token, CancellationToken cancellationToken = default)
        => _context.EnrollmentTokens.InsertOneAsync(token, cancellationToken: cancellationToken);

    public Task UpdateAsync(EnrollmentToken token, CancellationToken cancellationToken = default)
        => _context.EnrollmentTokens.ReplaceOneAsync(
            existing => existing.Id == token.Id, token, cancellationToken: cancellationToken);

    public async Task<bool> TryClaimAsync(Guid tokenId, Guid deviceId, DateTime now, CancellationToken cancellationToken = default)
    {
        var filter = Builders<EnrollmentToken>.Filter;

        // One use left, still on: counted and taken in one write. A token made
        // before T-327 carries its one device without a count, and is used.
        var claimed = await _context.EnrollmentTokens.UpdateOneAsync(
                filter.Eq(token => token.Id, tokenId)
                & filter.Eq(token => token.RevokedAt, null)
                & filter.Gt(token => token.ExpiresAt, now)
                & filter.Where(token => token.UsedCount < token.MaxUses)
                & (filter.Eq(token => token.UsedByDeviceId, null) | filter.Gte(token => token.UsedCount, 1)),
                Builders<EnrollmentToken>.Update
                    .Inc(token => token.UsedCount, 1)
                    .Push(token => token.UsedByDeviceIds, deviceId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (claimed.ModifiedCount == 0)
        {
            return false;
        }

        await _context.EnrollmentTokens.UpdateOneAsync(
                filter.Eq(token => token.Id, tokenId) & filter.Eq(token => token.UsedByDeviceId, null),
                Builders<EnrollmentToken>.Update.Set(token => token.UsedByDeviceId, deviceId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}

/// <inheritdoc cref="IKioskRepository" />
/// <remarks>
/// A change of status is announced to the open panels as it is saved
/// (<c>KioskStatusChanged</c> on <c>/hubs/admin</c>), the same way its
/// transition is written: here, so that no handler can forget it. A panel left
/// open showed a kiosk as Online for as long as nobody reloaded the page
/// whenever the change came from a handler that did not say so itself - an
/// administrator's pause, a test page that took the kiosk into service, a
/// device bound or released (T-248).
/// </remarks>
public sealed class KioskRepository : IKioskRepository
{
    private readonly MongoContext _context;
    private readonly IAdminNotifier? _admins;

    /// <param name="context">The database.</param>
    /// <param name="admins">The panels; absent in a host that serves none.</param>
    public KioskRepository(MongoContext context, IAdminNotifier? admins = null)
    {
        _context = context;
        _admins = admins;
    }

    public Task<Kiosk?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Kiosks.Find(kiosk => kiosk.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<Kiosk?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
        => _context.Kiosks.Find(kiosk => kiosk.Code == code).FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlyList<Kiosk>> ListAsync(CancellationToken cancellationToken = default)
        => await _context.Kiosks
            .Find(FilterDefinition<Kiosk>.Empty)
            .SortBy(kiosk => kiosk.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> GetHighestCodeSequenceAsync(CancellationToken cancellationToken = default)
    {
        // Sorting by code descending gives the highest KSK-nnnn, since the
        // numeric part is zero padded to a fixed width. Only codes of that
        // shape: one like KSK-T12345 sorts above every number and would make
        // the next code KSK-0001 again - a duplicate, and a failed activation.
        var codes = await _context.Kiosks
            .Find(Builders<Kiosk>.Filter.Regex(kiosk => kiosk.Code, new MongoDB.Bson.BsonRegularExpression("^KSK-[0-9]{4}$")))
            .Project(kiosk => kiosk.Code)
            .SortByDescending(kiosk => kiosk.Code)
            .Limit(1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var highest = codes.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(highest) || !highest.StartsWith("KSK-", StringComparison.Ordinal))
        {
            return 0;
        }

        return int.TryParse(
            highest.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : 0;
    }

    public Task AddAsync(Kiosk kiosk, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.Kiosks, kiosk, cancellationToken);

    public async Task UpdateAsync(Kiosk kiosk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kiosk);

        // Asked before the save: saving drains the transitions.
        var statusChanged = kiosk.PendingTransitions.Count > 0;

        await AggregateWriter.ReplaceAsync(_context, _context.Kiosks, kiosk, cancellationToken).ConfigureAwait(false);

        if (statusChanged && _admins is not null)
        {
            await _admins.KioskStatusChangedAsync(kiosk.Id, kiosk.Status.ToString(), cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.Kiosks.DeleteOneAsync(kiosk => kiosk.Id == id, cancellationToken);
}

/// <inheritdoc cref="IAuditWriter" />
/// <remarks>
/// The correlation id is taken from the ambient context rather than from the
/// entry, so an audited action is tied to the request that caused it without
/// every call site having to remember to pass it
/// (<c>docs/OBSERVABILITY.md</c>).
/// </remarks>
public sealed class MongoAuditWriter : IAuditWriter
{
    private readonly MongoContext _context;
    private readonly ICorrelationContext _correlation;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public MongoAuditWriter(
        MongoContext context, ICorrelationContext correlation, ICurrentUser currentUser, IClock clock)
    {
        _context = context;
        _correlation = correlation;
        _currentUser = currentUser;
        _clock = clock;
    }

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return _context.AuditLogs.InsertOneAsync(
            new AuditLog
            {
                AdminUserId = entry.AdminUserId,
                ActorType = entry.ActorType,
                CorrelationId = _correlation.CorrelationId,
                Action = entry.Action,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                Before = entry.Before,
                After = entry.After,
                Ip = entry.Ip,
                // The handler's own reason when it has one; otherwise the one
                // the administrator gave the request (X-Audit-Reason).
                Reason = entry.Reason ?? _currentUser.AuditReason,
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken);
    }
}
