using System.Globalization;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.Wallets;

namespace NeoPrint.Application.Wallets;

/// <summary>Step one of a top-up: the payment and its card reference, committed before the card is charged.</summary>
public sealed record OpenTopUpCommand(Guid UserId, decimal Amount, string? IdempotencyKey) : IRequest<AppPaymentPlan>, ITransactionalRequest;

/// <summary>The wallet (plan sections 2.14, 6 "Wallet").</summary>
/// <remarks>
/// There is deliberately no command here - and none anywhere - that moves a
/// balance to a card or out of the system: the wallet is prepaid service
/// credit and is never paid back (plan section 2.14). A test holds the API to that.
/// </remarks>
public sealed class WalletHandlers :
    IRequestHandler<GetWalletQuery, WalletView>,
    IRequestHandler<ListWalletTransactionsQuery, WalletTransactionPage>,
    IRequestHandler<TopUpWalletCommand, TopUpResponse>,
    IRequestHandler<OpenTopUpCommand, AppPaymentPlan>,
    IRequestHandler<AdjustWalletCommand, WalletView>
{
    /// <summary>A top-up's bounds, in manat.</summary>
    public const decimal MinTopUp = 1m;

    public const decimal MaxTopUp = 500m;

    /// <summary>A top-up the card never answered is looked at again by the reconciliation job after this.</summary>
    private static readonly TimeSpan TopUpWindow = TimeSpan.FromMinutes(15);

    private readonly ISender _sender;
    private readonly WalletLedger _wallet;
    private readonly IWalletTransactionRepository _ledger;
    private readonly IPaymentRepository _payments;
    private readonly IPaymentProviders _providers;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<WalletHandlers> _logger;

    public WalletHandlers(
        ISender sender,
        WalletLedger wallet,
        IWalletTransactionRepository ledger,
        IPaymentRepository payments,
        IPaymentProviders providers,
        IAuditWriter audit,
        IClock clock,
        ILogger<WalletHandlers> logger)
    {
        _sender = sender;
        _wallet = wallet;
        _ledger = ledger;
        _payments = payments;
        _providers = providers;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="GetWalletQuery" />
    public async Task<WalletView> Handle(GetWalletQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return View(await _wallet.ReadAsync(request.UserId, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc cref="ListWalletTransactionsQuery" />
    public async Task<WalletTransactionPage> Handle(ListWalletTransactionsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var (items, total) = await _ledger.ListAsync(request.UserId, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);

        return new WalletTransactionPage(
            items.Select(entry => new WalletTransactionView(
                    entry.Id,
                    entry.Type.ToString(),
                    entry.Amount.Amount,
                    entry.BonusPages,
                    entry.BalanceAfter.Amount,
                    entry.BonusPagesAfter,
                    entry.CreatedAt,
                    entry.Note))
                .ToList(),
            total,
            page,
            size);
    }

    /// <inheritdoc cref="TopUpWalletCommand" />
    public async Task<TopUpResponse> Handle(TopUpWalletCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = await _sender.Send(new OpenTopUpCommand(request.UserId, request.Amount, request.IdempotencyKey), cancellationToken)
            .ConfigureAwait(false);

        if (plan.CardReference is { } reference)
        {
            await _providers.Card.ChargeAsync(reference, Money.FromAzn(plan.CardAmount), cancellationToken).ConfigureAwait(false);

            if (await _sender.Send(new SettleCardPaymentCommand(plan.PaymentId), cancellationToken).ConfigureAwait(false) == SettleCardOutcome.Declined)
            {
                throw new DomainException("payments.declined");
            }
        }

        var payment = await _payments.FindByIdAsync(plan.PaymentId, cancellationToken).ConfigureAwait(false);
        var wallet = await _wallet.ReadAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        return new TopUpResponse(plan.PaymentId, payment?.Status.ToString() ?? nameof(PaymentStatus.Pending), View(wallet));
    }

    /// <inheritdoc cref="OpenTopUpCommand" />
    public async Task<AppPaymentPlan> Handle(OpenTopUpCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Amount is < MinTopUp or > MaxTopUp || decimal.Round(request.Amount, 2) != request.Amount)
        {
            throw new ValidationException([new ValidationError("amount", UserMessage.Of("wallet.topUpRange", ("min", MinTopUp), ("max", MaxTopUp)))]);
        }

        var now = _clock.UtcNow;
        var payment = Payment.OpenTopUp(request.UserId, Money.FromAzn(request.Amount), now, now + TopUpWindow, request.IdempotencyKey);
        var reference = $"MC-{payment.Id:N}-1";

        payment.AttachProvider(_providers.Card.Name, reference, null, now);
        await _payments.AddAsync(payment, cancellationToken).ConfigureAwait(false);

        return new AppPaymentPlan(payment.Id, false, reference, payment.Amount.Amount);
    }

    /// <inheritdoc cref="AdjustWalletCommand" />
    public async Task<WalletView> Handle(AdjustWalletCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new ValidationException([new ValidationError("reason", "wallet.adjustReasonRequired")]);
        }

        if (decimal.Round(request.Amount, 2) != request.Amount)
        {
            throw new ValidationException([new ValidationError("amount", "wallet.amountPrecision")]);
        }

        var before = await _wallet.ReadAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var (entry, applied) = await _wallet
            .PostAsync(
                request.UserId,
                WalletTransactionType.AdminAdjust,
                Money.FromAzn(request.Amount),
                request.BonusPages,
                request.AdminUserId.ToString(),
                $"adjust:{request.IdempotencyKey ?? Guid.NewGuid().ToString("N")}",
                request.AdminUserId,
                _clock.UtcNow,
                request.Reason.Trim(),
                cancellationToken)
            .ConfigureAwait(false);

        if (applied)
        {
            await _audit.WriteAsync(
                    new AuditEntry(
                        request.AdminUserId,
                        "wallets.adjust",
                        "Wallet",
                        request.UserId.ToString(),
                        Format(before.Balance, before.BonusPages),
                        Format(entry.BalanceAfter, entry.BonusPagesAfter),
                        request.Ip,
                        request.Reason.Trim()),
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Admin {AdminId} adjusted wallet {UserId} by {Amount} and {Pages} bonus pages.",
                request.AdminUserId,
                request.UserId,
                request.Amount,
                request.BonusPages);
        }

        return View(await _wallet.ReadAsync(request.UserId, cancellationToken).ConfigureAwait(false));
    }

    private static WalletView View(Wallet wallet) => new(wallet.Balance.Amount, wallet.BonusPages, wallet.Balance.Currency);

    private static string Format(Money balance, int pages)
        => string.Create(CultureInfo.InvariantCulture, $"{{\"balance\":{balance.Amount},\"bonusPages\":{pages}}}");
}
