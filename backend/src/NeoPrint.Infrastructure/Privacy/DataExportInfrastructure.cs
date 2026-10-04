using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using MassTransit;

using MediatR;

using Microsoft.Extensions.Configuration;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Privacy;
using NeoPrint.Contracts;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Privacy;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Infrastructure.Privacy;

/// <inheritdoc cref="IDataExportRepository" />
public sealed class DataExportRepository : IDataExportRepository
{
    private readonly MongoContext _context;

    public DataExportRepository(MongoContext context) => _context = context;

    public Task<DataExport?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _context.DataExports.Find(e => e.Id == id).FirstOrDefaultAsync(cancellationToken)!;

    public Task<DataExport?> FindLatestAsync(Guid userId, CancellationToken cancellationToken = default)
        => _context.DataExports.Find(e => e.UserId == userId).SortByDescending(e => e.CreatedAt).FirstOrDefaultAsync(cancellationToken)!;

    public Task InsertAsync(DataExport export, CancellationToken cancellationToken = default)
        => AggregateWriter.InsertAsync(_context, _context.DataExports, export, cancellationToken);

    public Task ReplaceAsync(DataExport export, CancellationToken cancellationToken = default)
        => AggregateWriter.ReplaceAsync(_context, _context.DataExports, export, cancellationToken);

    public async Task<IReadOnlyList<DataExport>> ListExpiredAsync(DateTime now, int take, CancellationToken cancellationToken = default)
        => await _context.DataExports
            .Find(e => e.Status == DataExportStatus.Ready && e.ExpiresAt <= now)
            .SortBy(e => e.ExpiresAt)
            .Limit(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<DataExport>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => await _context.DataExports.Find(e => e.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false);
}

/// <inheritdoc cref="IExportLinkSigner" />
/// <remarks>
/// <c>{export id}{expiry, Unix seconds}{HMAC-SHA256}</c>, base64url. The key is
/// derived (HKDF, own label) from <c>NEOPRINT_DATA_PROTECTION_KEY</c> in
/// OpenBao, so it is not any other feature's key. The token travels in the
/// query string, which is not logged (<c>Microsoft.AspNetCore</c> at Warning).
/// </remarks>
public sealed class HmacExportLinkSigner : IExportLinkSigner
{
    private const int IdLength = 16;
    private const int ExpiryLength = 8;
    private const int MacLength = 32;

    private readonly IConfiguration _configuration;

    public HmacExportLinkSigner(IConfiguration configuration) => _configuration = configuration;

    public string Sign(Guid exportId, DateTime expiresAt)
    {
        var payload = new byte[IdLength + ExpiryLength];
        exportId.TryWriteBytes(payload.AsSpan(0, IdLength));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(IdLength), new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds());

        var token = new byte[payload.Length + MacLength];
        payload.CopyTo(token, 0);
        HMACSHA256.HashData(Key(), payload, token.AsSpan(payload.Length));

        return Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public Guid? Verify(string token, DateTime now)
    {
        byte[] bytes;
        try
        {
            var base64 = (token ?? string.Empty).Replace('-', '+').Replace('_', '/');
            bytes = Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length != IdLength + ExpiryLength + MacLength)
        {
            return null;
        }

        var payload = bytes.AsSpan(0, IdLength + ExpiryLength);
        var expected = HMACSHA256.HashData(Key(), payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(IdLength + ExpiryLength)))
        {
            return null;
        }

        var expires = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(payload[IdLength..])).UtcDateTime;
        return now < expires ? new Guid(payload[..IdLength]) : null;
    }

    private byte[] Key()
    {
        var value = _configuration["NEOPRINT_DATA_PROTECTION_KEY"];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("NEOPRINT_DATA_PROTECTION_KEY is not configured; export links cannot be signed (docs/OPENBAO.md).");
        }

        byte[] root;
        try
        {
            root = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            root = Encoding.UTF8.GetBytes(value);
        }

        if (root.Length < 32)
        {
            throw new InvalidOperationException("NEOPRINT_DATA_PROTECTION_KEY is shorter than 256 bits.");
        }

        return HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, info: Encoding.ASCII.GetBytes("neoprint-export-download"));
    }
}

/// <inheritdoc cref="IDataExportBuilder" />
/// <remarks>
/// Reads the user's own documents (never changes another module's collection)
/// and the stored objects through <see cref="IFileStorage"/>, decrypted into the
/// ZIP. No secrets: no password hash, tokens, push tokens or provider references.
/// </remarks>
public sealed class DataExportBuilder : IDataExportBuilder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly MongoContext _context;
    private readonly IFileStorage _storage;

    public DataExportBuilder(MongoContext context, IFileStorage storage)
    {
        _context = context;
        _storage = storage;
    }

    public async Task<DataExportContents> BuildAsync(
        Guid userId, string language, long maxFileBytes, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var user = await _context.Users.Find(u => u.Id == userId).SingleAsync(cancellationToken).ConfigureAwait(false);
        var consents = await _context.Consents.Find(c => c.UserId == userId).SortBy(c => c.AcceptedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var payments = await _context.Payments.Find(p => p.UserId == userId).SortBy(p => p.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var refunds = await _context.Refunds.Find(r => r.UserId == userId).SortBy(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var wallet = await _context.Wallets.Find(w => w.Id == userId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var movements = await _context.WalletTransactions.Find(t => t.UserId == userId).SortBy(t => t.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await _context.PrintJobs.Find(j => j.UserId == userId).SortBy(j => j.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var receipts = await _context.Receipts.Find(r => r.UserId == userId).SortBy(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var files = await _context.Files.Find(f => f.UserId == userId && f.DeletedAt == null).SortBy(f => f.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);

        var fileBytes = files.Sum(f => f.Original.PlaintextLength + f.Versions.Sum(v => v.Object.PlaintextLength));
        var includeFiles = fileBytes <= maxFileBytes;

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);

        await Text(zip, "README.txt", Messages.Write(new Domain.Common.UserMessage(
            includeFiles ? "privacy.export.readme" : "privacy.export.readmeWithoutFiles"), language)).ConfigureAwait(false);

        await JsonEntry(zip, "profile", new
        {
            user.PublicId,
            user.Email,
            user.FullName,
            user.Language,
            user.EmailConfirmed,
            user.ReferralCode,
            RegisteredAt = user.CreatedAt,
            Consents = consents.Select(c => new { c.DocType, c.Version, c.AcceptedAt }),
        }).ConfigureAwait(false);

        await JsonEntry(zip, "payments", payments.Select(p => new
        {
            p.Id,
            p.CreatedAt,
            p.Purpose,
            p.Method,
            p.Channel,
            p.Status,
            Amount = p.Amount.Amount,
            Currency = p.Amount.Currency,
            Discount = p.Discount.Amount,
            p.PromoCode,
            Refunded = p.RefundedAmount.Amount,
            p.JobId,
        })).ConfigureAwait(false);

        await JsonEntry(zip, "refunds", refunds.Select(r => new
        {
            r.Id,
            r.CreatedAt,
            r.PaymentId,
            r.JobId,
            Amount = r.Amount.Amount,
            r.BonusPages,
            r.Destination,
            r.Reason,
            r.Status,
            r.VoucherCode,
        })).ConfigureAwait(false);

        await JsonEntry(zip, "balance", new
        {
            Balance = wallet?.Balance.Amount ?? 0m,
            BonusPages = wallet?.BonusPages ?? 0,
            Movements = movements.Select(t => new
            {
                t.CreatedAt,
                t.Type,
                Amount = t.Amount.Amount,
                t.BonusPages,
                BalanceAfter = t.BalanceAfter.Amount,
                t.BonusPagesAfter,
            }),
        }).ConfigureAwait(false);

        await JsonEntry(zip, "orders", jobs.Select(j => new
        {
            j.Id,
            j.JobCode,
            j.CreatedAt,
            j.Status,
            j.PageCount,
            Amount = j.Price?.Total.Amount,
            j.FileId,
        })).ConfigureAwait(false);

        foreach (var receipt in receipts.Where(r => r.Pdf is not null))
        {
            await Copy(zip, $"receipts/{Safe(receipt.Number)}.pdf", receipt.Pdf!, StorageBucket.Receipts, cancellationToken).ConfigureAwait(false);
        }

        await JsonEntry(zip, "files", files.Select(f => new
        {
            f.Id,
            f.FileName,
            f.CreatedAt,
            f.Source,
            Bytes = f.Original.PlaintextLength,
            f.IsSensitive,
            f.AutoDelete,
            Versions = f.Versions.Select(v => new { v.Number, v.CreatedAt, v.Note, Bytes = v.Object.PlaintextLength }),
        })).ConfigureAwait(false);

        if (includeFiles)
        {
            var index = 0;
            foreach (var file in files)
            {
                index++;
                var stem = $"files/{index:000} {Safe(Path.GetFileNameWithoutExtension(file.FileName))}";
                await Copy(zip, $"{stem}.{file.Extension}", file.Original, StorageBucket.Files, cancellationToken).ConfigureAwait(false);

                foreach (var version in file.Versions.OrderBy(v => v.Number))
                {
                    await Copy(zip, $"{stem} (v{version.Number}).{version.Extension ?? file.Extension}", version.Object, StorageBucket.Files, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        return new DataExportContents(includeFiles);
    }

    private static async Task Text(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
    }

    /// <summary><paramref name="name"/>.json at the top of the archive.</summary>
    private static async Task JsonEntry(ZipArchive zip, string name, object value)
    {
        var entry = zip.CreateEntry(name + ".json", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, Json).ConfigureAwait(false);
    }

    private async Task Copy(ZipArchive zip, string name, FileObject source, StorageBucket bucket, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await _storage.ReadAsync(DataExportHandlers.Stored(source), bucket, stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A name that is a name inside the archive, never a path.</summary>
    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c)]).Trim().Trim('.');
        return cleaned.Length == 0 ? "file" : cleaned[..Math.Min(cleaned.Length, 120)];
    }
}

/// <summary>A "Məlumatlarımı yüklə" request is made into its archive (plan section 6, <c>DataExportJob</c>).</summary>
public sealed class DataExportConsumer : IConsumer<DataExportRequested>
{
    private readonly ISender _sender;

    public DataExportConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<DataExportRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new PrepareDataExportCommand(context.Message.ExportId), context.CancellationToken);
    }
}

/// <summary>Every hour: archives past their 24 hours are deleted from storage.</summary>
public sealed class DataExportCleanupJob
{
    public const string RecurringId = "data-export-cleanup";

    private readonly ISender _sender;

    public DataExportCleanupJob(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
    public Task RunAsync(CancellationToken cancellationToken)
        => _sender.Send(new ExpireDataExportsCommand(), cancellationToken);
}
