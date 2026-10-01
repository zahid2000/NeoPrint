using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Files;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Wallets;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Customers;

/// <summary>
/// Users whose access tokens stop working before they expire (plan section
/// 8A.3: blocking takes effect now, not in fifteen minutes). Held in Redis for
/// longer than an access token lives; the refresh path checks the account
/// itself, so nothing outlives the entry.
/// </summary>
public interface IUserBlocklist
{
    Task BlockAsync(Guid userId, TimeSpan duration, CancellationToken cancellationToken = default);

    Task UnblockAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<bool> IsBlockedAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>What the admin list of customers filters on.</summary>
/// <param name="Search">Part of the email, or the exact public id (<c>NP-XXXXXX</c>).</param>
public sealed record CustomerFilter(string? Search, UserStatus? Status);

public sealed record CustomerRow(
    Guid Id,
    string PublicId,
    string Email,
    string? FullName,
    string Status,
    bool EmailConfirmed,
    DateTime CreatedAt,
    DateTime? BlockedAt);

public sealed record CustomerPage(IReadOnlyList<CustomerRow> Items, long Total, int Page, int PageSize);

public sealed record CustomerConsent(string DocType, string Version);

/// <summary>
/// One customer for the admin (plan section 8A.3): the profile, the wallet,
/// recent jobs, file totals (never contents), referrals and consents.
/// </summary>
public sealed record CustomerDetail(
    CustomerRow Profile,
    string Language,
    WalletView Wallet,
    IReadOnlyList<MyPrintJobView> RecentJobs,
    long FileCount,
    long FileBytes,
    long Referred,
    IReadOnlyList<CustomerConsent> Consents);

public sealed record ListCustomersQuery(string? Search, string? Status, int Page, int PageSize) : IRequest<CustomerPage>;

public sealed record GetCustomerQuery(Guid UserId) : IRequest<CustomerDetail>;

/// <summary>Blocks a customer: the account, its sessions and its live tokens.</summary>
public sealed record BlockCustomerCommand(Guid AdminUserId, Guid UserId, string? Reason, string? Ip) : IRequest<CustomerRow>;

public sealed record UnblockCustomerCommand(Guid AdminUserId, Guid UserId, string? Reason, string? Ip) : IRequest<CustomerRow>;

/// <summary>The database half of <see cref="BlockCustomerCommand"/>, in one transaction.</summary>
public sealed record RecordCustomerBlockCommand(Guid AdminUserId, Guid UserId, bool Blocked, string Reason, string? Ip)
    : IRequest<CustomerRow>, ITransactionalRequest;

public sealed class AdminCustomerHandlers :
    IRequestHandler<ListCustomersQuery, CustomerPage>,
    IRequestHandler<GetCustomerQuery, CustomerDetail>,
    IRequestHandler<BlockCustomerCommand, CustomerRow>,
    IRequestHandler<UnblockCustomerCommand, CustomerRow>,
    IRequestHandler<RecordCustomerBlockCommand, CustomerRow>
{
    /// <summary>Longer than an access token lives (15 minutes), so none outlasts the entry.</summary>
    private static readonly TimeSpan BlocklistFor = TimeSpan.FromMinutes(20);

    private const int MaxPageSize = 100;
    private const int MaxReasonLength = 500;

    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ILegalDocumentRepository _legal;
    private readonly IFileRepository _files;
    private readonly WalletLedger _wallets;
    private readonly ISender _sender;
    private readonly IUserBlocklist _blocklist;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public AdminCustomerHandlers(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        ILegalDocumentRepository legal,
        IFileRepository files,
        WalletLedger wallets,
        ISender sender,
        IUserBlocklist blocklist,
        IAuditWriter audit,
        IClock clock)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _legal = legal;
        _files = files;
        _wallets = wallets;
        _sender = sender;
        _blocklist = blocklist;
        _audit = audit;
        _clock = clock;
    }

    /// <inheritdoc cref="ListCustomersQuery" />
    public async Task<CustomerPage> Handle(ListCustomersQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        UserStatus? status = null;

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            status = Enum.TryParse<UserStatus>(request.Status, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ValidationException([new ValidationError("status", "users.statusInvalid")]);
        }

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, MaxPageSize);
        var (items, total) = await _users
            .SearchAsync(new CustomerFilter(request.Search?.Trim(), status), (page - 1) * size, size, cancellationToken)
            .ConfigureAwait(false);

        return new CustomerPage(items.Select(Row).ToList(), total, page, size);
    }

    /// <inheritdoc cref="GetCustomerQuery" />
    public async Task<CustomerDetail> Handle(GetCustomerQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await FindAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var wallet = await _wallets.ReadAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var jobs = await _sender.Send(new ListMyPrintJobsQuery(user.Id), cancellationToken).ConfigureAwait(false);
        var consents = await _legal.GetAcceptedVersionsAsync(user.Id, cancellationToken).ConfigureAwait(false);

        return new CustomerDetail(
            Row(user),
            user.Language,
            new WalletView(wallet.Balance.Amount, wallet.BonusPages, wallet.Balance.Currency),
            jobs.Take(20).ToList(),
            await _files.CountAsync(user.Id, cancellationToken).ConfigureAwait(false),
            await _files.TotalBytesAsync(user.Id, cancellationToken).ConfigureAwait(false),
            await _users.CountReferredAsync(user.Id, cancellationToken).ConfigureAwait(false),
            consents.Select(c => new CustomerConsent(c.Key.ToString(), c.Value)).OrderBy(c => c.DocType, StringComparer.Ordinal).ToList());
    }

    /// <inheritdoc cref="BlockCustomerCommand" />
    public async Task<CustomerRow> Handle(BlockCustomerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var row = await _sender
            .Send(new RecordCustomerBlockCommand(request.AdminUserId, request.UserId, true, Reason(request.Reason), request.Ip), cancellationToken)
            .ConfigureAwait(false);

        // After the commit: a transaction that rolled back must not leave the
        // account locked out. A Redis that is down still leaves the refresh
        // refused, so the longest a token lives on is its own 15 minutes.
        await _blocklist.BlockAsync(request.UserId, BlocklistFor, cancellationToken).ConfigureAwait(false);

        return row;
    }

    /// <inheritdoc cref="UnblockCustomerCommand" />
    public async Task<CustomerRow> Handle(UnblockCustomerCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var row = await _sender
            .Send(new RecordCustomerBlockCommand(request.AdminUserId, request.UserId, false, Reason(request.Reason), request.Ip), cancellationToken)
            .ConfigureAwait(false);

        await _blocklist.UnblockAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        return row;
    }

    /// <inheritdoc cref="RecordCustomerBlockCommand" />
    public async Task<CustomerRow> Handle(RecordCustomerBlockCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await FindAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var before = user.Status.ToString();
        var changed = request.Blocked ? user.Block(now) : user.Unblock();

        // Blocking twice, or unblocking an active account, is a retried request:
        // the same answer, nothing written twice.
        if (!changed)
        {
            return Row(user);
        }

        await _users.UpdateAsync(user, cancellationToken).ConfigureAwait(false);

        if (request.Blocked)
        {
            // Every session ends: the phone cannot refresh its way back in.
            await _refreshTokens.RevokeAllForUserAsync(user.Id, now, cancellationToken).ConfigureAwait(false);
        }

        await _audit
            .WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    request.Blocked ? "users.block" : "users.unblock",
                    "User",
                    user.Id.ToString(),
                    before,
                    user.Status.ToString(),
                    request.Ip,
                    request.Reason),
                cancellationToken)
            .ConfigureAwait(false);

        return Row(user);
    }

    private async Task<User> FindAsync(Guid userId, CancellationToken cancellationToken)
        => await _users.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("user");

    /// <summary>A reason is required and audited (plan section 8A.1).</summary>
    private static string Reason(string? reason)
    {
        var trimmed = reason?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxReasonLength)
        {
            throw new ValidationException([new ValidationError("reason", UserMessage.Of("users.reasonRequired", ("max", MaxReasonLength)))]);
        }

        return trimmed;
    }

    private static CustomerRow Row(User user) => new(
        user.Id,
        user.PublicId,
        user.Email,
        user.FullName,
        user.Status.ToString(),
        user.EmailConfirmed,
        user.CreatedAt,
        user.BlockedAt);
}
