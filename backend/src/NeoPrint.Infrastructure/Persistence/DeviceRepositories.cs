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
}

/// <inheritdoc cref="IKioskRepository" />
public sealed class KioskRepository : IKioskRepository
{
    private readonly MongoContext _context;

    public KioskRepository(MongoContext context) => _context = context;

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
        // numeric part is zero padded to a fixed width.
        var codes = await _context.Kiosks
            .Find(FilterDefinition<Kiosk>.Empty)
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

    public Task UpdateAsync(Kiosk kiosk, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.Kiosks, kiosk, cancellationToken);

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
    private readonly IClock _clock;

    public MongoAuditWriter(MongoContext context, ICorrelationContext correlation, IClock clock)
    {
        _context = context;
        _correlation = correlation;
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
                Reason = entry.Reason,
                CreatedAt = _clock.UtcNow,
            },
            cancellationToken: cancellationToken);
    }
}
