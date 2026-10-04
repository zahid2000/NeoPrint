using MediatR;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Files;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Privacy;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Privacy;

/// <summary>
/// What an account deletion needs from the records other parts keep about the
/// user (plan section 2.13; <c>docs/DECISIONS.md</c> T-147).
/// </summary>
/// <remarks>
/// One place for a process that crosses modules: what still holds the user's
/// money or orders (the conditions), and the personal details that are scrubbed
/// from records that themselves stay - receipts, consents, support claims - or
/// removed with the person - notifications. Payments, refunds, fiscal receipts
/// and the balance ledger are not touched: they stay, tied to the anonymous
/// account (plan section 2.13).
/// </remarks>
public interface IPrivacyRecords
{
    Task<DeletionBlockers> BlockersAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Emails on receipts, IPs and devices on consents, texts of claims; notifications removed. Repeatable.</summary>
    Task ScrubAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>What keeps an account from being deleted (<c>docs/DECISIONS.md</c> T-147).</summary>
/// <param name="ActivePrints">Orders queued or printing at a kiosk.</param>
/// <param name="ScheduledPrints">Scheduled prints still waiting or due.</param>
/// <param name="UncollectedOrders">Paid in the app, not yet printed at the kiosk ("Hazırladıqlarım").</param>
/// <param name="PendingRefunds">Money on its way back.</param>
public sealed record DeletionBlockers(int ActivePrints, int ScheduledPrints, int UncollectedOrders, int PendingRefunds)
{
    public bool Any => ActivePrints + ScheduledPrints + UncollectedOrders + PendingRefunds > 0;
}

/// <summary><c>GET users/me/delete</c>: what stands in the way, and what would be lost.</summary>
public sealed record GetDeletionCheckQuery(Guid UserId) : IRequest<DeletionCheck>;

/// <param name="Balance">Lost with the account (plan section 2.14).</param>
public sealed record DeletionCheck(DeletionBlockers Blockers, decimal Balance, string Currency, int BonusPages);

/// <summary>
/// <c>POST users/me/delete</c>: the password and the typed confirmation; the
/// account closes at once, its files go after <c>Privacy.AccountDeletionGraceHours</c>.
/// </summary>
public sealed record DeleteAccountCommand(Guid UserId, string Password, string Confirmation) : IRequest, ITransactionalRequest;

/// <summary>Every ten minutes (plan section 6, <c>AccountDeletionJob</c>): accounts past their grace hours are erased.</summary>
public sealed record EraseDeletedAccountsCommand : IRequest<int>;

/// <summary>One deleted account erased: files on every provider, archives, personal details.</summary>
public sealed record EraseAccountCommand(Guid UserId) : IRequest;

public sealed class AccountDeletionHandlers :
    IRequestHandler<GetDeletionCheckQuery, DeletionCheck>,
    IRequestHandler<DeleteAccountCommand>,
    IRequestHandler<EraseDeletedAccountsCommand, int>,
    IRequestHandler<EraseAccountCommand>
{
    /// <summary>"SİL", in any of the app's languages.</summary>
    private static readonly HashSet<string> Confirmations = new(StringComparer.OrdinalIgnoreCase) { "SİL", "SIL", "DELETE", "УДАЛИТЬ" };

    private const int Batch = 20;

    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _tokens;
    private readonly Customers.IUserBlocklist _blocklist;
    private readonly IPasswordHasher<User> _passwords;
    private readonly IPrivacyRecords _records;
    private readonly Wallets.IWalletRepository _wallets;
    private readonly IFileRepository _files;
    private readonly FileEraser _eraser;
    private readonly IDataExportRepository _exports;
    private readonly IFileStorage _storage;
    private readonly ISettingsReader _settings;
    private readonly ISender _sender;
    private readonly IClock _clock;
    private readonly ILogger<AccountDeletionHandlers> _logger;

    public AccountDeletionHandlers(
        IUserRepository users,
        IRefreshTokenRepository tokens,
        Customers.IUserBlocklist blocklist,
        IPasswordHasher<User> passwords,
        IPrivacyRecords records,
        Wallets.IWalletRepository wallets,
        IFileRepository files,
        FileEraser eraser,
        IDataExportRepository exports,
        IFileStorage storage,
        ISettingsReader settings,
        ISender sender,
        IClock clock,
        ILogger<AccountDeletionHandlers> logger)
    {
        _users = users;
        _tokens = tokens;
        _blocklist = blocklist;
        _passwords = passwords;
        _records = records;
        _wallets = wallets;
        _files = files;
        _eraser = eraser;
        _exports = exports;
        _storage = storage;
        _settings = settings;
        _sender = sender;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DeletionCheck> Handle(GetDeletionCheckQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var blockers = await _records.BlockersAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var wallet = await _wallets.FindAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var balance = wallet?.Balance ?? Money.Zero;
        return new DeletionCheck(blockers, balance.Amount, balance.Currency, wallet?.BonusPages ?? 0);
    }

    public async Task Handle(DeleteAccountCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("user");

        if (!Confirmations.Contains(request.Confirmation?.Trim() ?? string.Empty))
        {
            throw new ValidationException([new ValidationError("confirmation", "privacy.deleteConfirmation")]);
        }

        if (string.IsNullOrEmpty(user.PasswordHash)
            || _passwords.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? string.Empty) == PasswordVerificationResult.Failed)
        {
            throw new ValidationException([new ValidationError("password", "privacy.deletePassword")]);
        }

        var blockers = await _records.BlockersAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (blockers.Any)
        {
            throw new DomainException("privacy.deleteBlocked");
        }

        var now = _clock.UtcNow;
        user.Delete(now);
        await _users.UpdateAsync(user, cancellationToken).ConfigureAwait(false);

        // Every session ends, and an access token still in hand is refused from
        // the next request on (it lives 15 minutes; the blocklist entry longer).
        await _tokens.RevokeAllForUserAsync(user.Id, now, cancellationToken).ConfigureAwait(false);
        await _blocklist.BlockAsync(user.Id, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Account {UserId} deleted by its owner; erased after the grace hours.", user.Id);
    }

    public async Task<int> Handle(EraseDeletedAccountsCommand request, CancellationToken cancellationToken)
    {
        var hours = Math.Clamp(
            await _settings.GetIntAsync(SettingKeys.PrivacyAccountDeletionGraceHours, 24, cancellationToken).ConfigureAwait(false), 0, 720);
        var erased = 0;

        foreach (var id in await _users.ListToEraseAsync(_clock.UtcNow.AddHours(-hours), Batch, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await _sender.Send(new EraseAccountCommand(id), cancellationToken).ConfigureAwait(false);
                erased++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A provider that would not let go of one file: the whole account is tried again next run.
                _logger.LogWarning(exception, "Erasing deleted account {UserId} failed; it is tried again.", id);
            }
        }

        return erased;
    }

    public async Task Handle(EraseAccountCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        if (user is not { Status: UserStatus.Deleted, ErasedAt: null })
        {
            return;
        }

        var files = 0;
        foreach (var fileId in await _files.ListLiveIdsAsync(user.Id, cancellationToken).ConfigureAwait(false))
        {
            if (await _files.FindByIdAsync(fileId, cancellationToken).ConfigureAwait(false) is { DeletedAt: null } file)
            {
                await _eraser.EraseAsync(file, cancellationToken).ConfigureAwait(false);
                files++;
            }
        }

        foreach (var export in await _exports.ListForUserAsync(user.Id, cancellationToken).ConfigureAwait(false))
        {
            if (export.Archive is { } archive)
            {
                await _storage.DeleteAsync(DataExportHandlers.Stored(archive), StorageBucket.Temp, cancellationToken).ConfigureAwait(false);
                if (export.Status == DataExportStatus.Ready)
                {
                    export.Expire(_clock.UtcNow);
                    await _exports.ReplaceAsync(export, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await _records.ScrubAsync(user.Id, cancellationToken).ConfigureAwait(false);

        user.ErasedAt = _clock.UtcNow;
        await _users.UpdateAsync(user, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Deleted account {UserId} erased: {Files} file(s) from every provider, personal details scrubbed.", user.Id, files);
    }
}
