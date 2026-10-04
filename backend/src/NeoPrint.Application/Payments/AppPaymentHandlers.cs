using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Application.Wallets;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Payments;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Settings;
using NeoPrint.Domain.Wallets;

namespace NeoPrint.Application.Payments;

/// <summary>
/// Paying at the kiosk with the NeoPrint app (plan section 9.2) and settling
/// card charges (top-ups and the card part of an app payment).
/// </summary>
/// <remarks>
/// The order of an app payment is what keeps the money safe: the wallet and
/// bonus parts are set aside first (a ledger line), then the card is charged
/// with a reference of this try, then the acquirer's own status decides. A
/// declined card gives the reservation back; a charge whose answer was lost
/// stays reserved until reconciliation learns the outcome - never released on
/// a guess, never charged twice.
/// </remarks>
public sealed class AppPaymentHandlers :
    IRequestHandler<GetAppPaymentQuery, AppPaymentView>,
    IRequestHandler<PayWithAppCommand, AppPaymentView>,
    IRequestHandler<ReserveAppPaymentCommand, AppPaymentPlan>,
    IRequestHandler<SettleCardPaymentCommand, SettleCardOutcome>
{
    private readonly ISender _sender;
    private readonly IPaymentRepository _payments;
    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly Schedules.IScheduledPrintRepository _schedules;
    private readonly IPaymentProviders _providers;
    private readonly PaymentSettlement _settlement;
    private readonly WalletLedger _wallet;
    private readonly Promos.PromoService _promos;
    private readonly Features.IFeatureFlagEvaluator _features;
    private readonly ISettingsReader _settings;
    private readonly ICorrelationContext _correlation;
    private readonly IClock _clock;
    private readonly ILogger<AppPaymentHandlers> _logger;

    public AppPaymentHandlers(
        ISender sender,
        IPaymentRepository payments,
        IPrintJobRepository jobs,
        IKioskRepository kiosks,
        Schedules.IScheduledPrintRepository schedules,
        IPaymentProviders providers,
        PaymentSettlement settlement,
        WalletLedger wallet,
        Promos.PromoService promos,
        Features.IFeatureFlagEvaluator features,
        ISettingsReader settings,
        ICorrelationContext correlation,
        IClock clock,
        ILogger<AppPaymentHandlers> logger)
    {
        _sender = sender;
        _payments = payments;
        _jobs = jobs;
        _kiosks = kiosks;
        _schedules = schedules;
        _providers = providers;
        _settlement = settlement;
        _wallet = wallet;
        _promos = promos;
        _features = features;
        _settings = settings;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="GetAppPaymentQuery" />
    public async Task<AppPaymentView> Handle(GetAppPaymentQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await AppPaymentAsync(request.UserId, request.PaymentId, cancellationToken).ConfigureAwait(false);

        if (payment is { WasPaid: false, Reserved: null })
        {
            await RequireKioskAppPaymentAsync(payment, request.UserId, cancellationToken).ConfigureAwait(false);
        }

        return await ViewAsync(payment, request.UserId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="PayWithAppCommand" />
    public async Task<AppPaymentView> Handle(PayWithAppCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = await _sender
            .Send(new ReserveAppPaymentCommand(request.UserId, request.PaymentId, request.Source, request.UseBonus, request.ExpectedAmount, request.PromoCode), cancellationToken)
            .ConfigureAwait(false);

        if (!plan.Settled && plan.CardReference is { } reference)
        {
            // Outside any transaction: the acquirer is another system. The
            // reference is this try's, so repeating the call cannot charge twice.
            await _providers.Card.ChargeAsync(reference, Money.FromAzn(plan.CardAmount), cancellationToken).ConfigureAwait(false);

            var outcome = await _sender.Send(new SettleCardPaymentCommand(request.PaymentId), cancellationToken).ConfigureAwait(false);

            if (outcome == SettleCardOutcome.Declined)
            {
                throw new DomainException("payments.declined");
            }
        }

        var payment = await AppPaymentAsync(request.UserId, request.PaymentId, cancellationToken).ConfigureAwait(false);

        return await ViewAsync(payment, request.UserId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ReserveAppPaymentCommand" />
    public async Task<AppPaymentPlan> Handle(ReserveAppPaymentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await AppPaymentAsync(request.UserId, request.PaymentId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        if (payment.WasPaid)
        {
            return new AppPaymentPlan(payment.Id, true, null, 0);
        }

        if (payment.Status != PaymentStatus.Pending || payment.ExpiresAt <= now)
        {
            throw new DomainException("payments.notPayable");
        }

        if (payment.Reserved is { } open)
        {
            // A try already in progress - its card answer was lost. Carry on
            // with the same reference rather than set more money aside.
            return new AppPaymentPlan(payment.Id, false, payment.ProviderRef, open.External.Amount);
        }

        await RequireKioskAppPaymentAsync(payment, request.UserId, cancellationToken).ConfigureAwait(false);

        var source = request.Source?.Trim() switch
        {
            { } s when s.Equals("Wallet", StringComparison.OrdinalIgnoreCase) => PaymentMethod.Wallet,
            { } s when s.Equals("Card", StringComparison.OrdinalIgnoreCase) => PaymentMethod.Card,
            _ => throw new ValidationException([new ValidationError("source", "payments.sourceInvalid")]),
        };

        var job = await JobOfAsync(payment, cancellationToken).ConfigureAwait(false);
        var wallet = await _wallet.ReadAsync(request.UserId, cancellationToken).ConfigureAwait(false);
        var (bonusPages, bonusValue) = request.UseBonus
            ? await BonusAsync(payment, job, wallet, cancellationToken).ConfigureAwait(false)
            : (0, Money.Zero);

        // Plan section 2.7: profile price → volume discount → bonus pages →
        // promo code on what is left; whatever remains is paid in money.
        var afterBonus = payment.Gross - bonusValue;
        var discount = Money.Zero;

        if (!string.IsNullOrWhiteSpace(request.PromoCode) && job.PromoCodeId is null && afterBonus.IsPositive)
        {
            // Typed in the app because none was given at the kiosk (one code per
            // order). Refused codes stop here, before any money is set aside.
            var typed = await _promos
                .CheckAsync(request.PromoCode, payment.KioskId, job.Price.ProfileId, afterBonus, request.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (typed.Refusal is { } refusal)
            {
                throw new ValidationException([new ValidationError("promoCode", refusal)]);
            }

            job.AttachPromo(typed.Promo!.Id, typed.Promo.Code, typed.Discount.Amount);
            await _jobs.UpdateAsync(job, cancellationToken).ConfigureAwait(false);
        }

        if (job is { PromoCodeId: { } promoId, PromoCode: { } code } && afterBonus.IsPositive
            && (await _promos.CheckAsync(code, payment.KioskId, job.Price.ProfileId, afterBonus, request.UserId, cancellationToken).ConfigureAwait(false))
                .Refusal is null)
        {
            // The same check the app's preview made; a race for the last place is a 409.
            discount = await _promos
                .ClaimAsync(promoId, payment.Id, payment.KioskId, job.Price.ProfileId, afterBonus, request.UserId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Not for this account (used up, not here): the full price, as the preview said.
            await _promos.ReleaseAsync(payment.Id, refunded: false, cancellationToken).ConfigureAwait(false);
        }

        payment.ApplyDiscount(job.PromoCodeId, job.PromoCode, discount, now);

        var rest = payment.Amount - bonusValue;

        if (request.ExpectedAmount is { } expected && expected != rest.Amount)
        {
            // The price moved since the app showed it: say so rather than take a different sum.
            throw new DomainException(UserMessage.Of("payments.priceChanged", ("expected", expected), ("actual", rest.Amount)));
        }
        var parts = source == PaymentMethod.Wallet
            ? new PaymentParts(Money.Zero, rest, bonusPages, bonusValue)
            : new PaymentParts(rest, Money.Zero, bonusPages, bonusValue);

        var method = rest.IsZero
            ? PaymentMethod.Bonus
            : bonusPages > 0 ? PaymentMethod.Mixed : source;

        payment.AssignUser(request.UserId, now);
        payment.Reserve(parts, method, now);

        if (parts.Wallet.IsPositive || parts.BonusPages > 0)
        {
            // Refuses with wallet.insufficient if the balance does not cover it;
            // the whole transaction goes back and nothing is set aside.
            await _wallet
                .PostAsync(
                    request.UserId,
                    WalletTransactionType.Payment,
                    -parts.Wallet,
                    -parts.BonusPages,
                    payment.Id.ToString(),
                    $"payment:{payment.Id}:{payment.ReservationAttempt}",
                    request.UserId,
                    now,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var actor = ActorContext.User(request.UserId, "payments.reason.paidWithApp", _correlation.CorrelationId);

        if (!parts.External.IsPositive)
        {
            await _settlement.SucceedAsync(payment, parts, method, now, actor, now, cancellationToken).ConfigureAwait(false);
            return new AppPaymentPlan(payment.Id, true, null, 0);
        }

        var reference = $"MC-{payment.Id:N}-{payment.ReservationAttempt}";
        payment.AttachProvider(_providers.Card.Name, reference, null, now);
        await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);

        return new AppPaymentPlan(payment.Id, false, reference, parts.External.Amount);
    }

    /// <inheritdoc cref="SettleCardPaymentCommand" />
    public async Task<SettleCardOutcome> Handle(SettleCardPaymentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payment = await _payments.FindByIdAsync(request.PaymentId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("payment");

        if (payment.WasPaid)
        {
            return SettleCardOutcome.Paid;
        }

        if (payment.ProviderRef is not { } reference || !string.Equals(payment.Provider, _providers.Card.Name, StringComparison.Ordinal))
        {
            return SettleCardOutcome.Unknown;
        }

        var status = await _providers.Card.GetStatusAsync(reference, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var actor = ActorContext.System("payments.reason.cardAnswered", _correlation.CorrelationId);

        switch (status.State)
        {
            case ProviderPaymentState.Succeeded:
                var parts = payment.Reserved ?? PaymentParts.ExternalOnly(payment.Amount);
                await _settlement
                    .SucceedAsync(payment, parts, payment.Method, status.PaidAt ?? now, actor, now, cancellationToken)
                    .ConfigureAwait(false);
                return SettleCardOutcome.Paid;

            case ProviderPaymentState.Failed when payment.Reserved is null && payment.Purpose == PaymentPurpose.PrintJob:
                // Already given back on an earlier answer; nothing more to do.
                return SettleCardOutcome.Declined;

            case ProviderPaymentState.Failed:
                await _settlement.ReleaseReservationAsync(payment, now, cancellationToken).ConfigureAwait(false);

                if (payment.Purpose == PaymentPurpose.WalletTopUp && payment.Status == PaymentStatus.Pending)
                {
                    payment.Fail("payments.declined", actor, now);
                    await _settlement.ReleasePromoAsync(payment, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // A kiosk's app payment stays open: the user may try again with the wallet.
                    payment.Note(now, "cardDeclined", status.FailureCode);
                }

                await _payments.UpdateAsync(payment, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Card declined for payment {PaymentId}.", payment.Id);
                return SettleCardOutcome.Declined;

            default:
                return SettleCardOutcome.Unknown;
        }
    }

    /// <summary>
    /// Bonus pages this order can take and what they are worth (plan section
    /// 2.6): one page is one printed side at the order's per-side price, at
    /// most <c>Referral.MaxBonusPagesPerJob</c>, never more than the order.
    /// </summary>
    private async Task<(int Pages, Money Value)> BonusAsync(Payment payment, PrintJob job, Wallet wallet, CancellationToken cancellationToken)
    {
        var max = await _settings.GetIntAsync(SettingKeys.ReferralMaxBonusPagesPerJob, 5, cancellationToken).ConfigureAwait(false);
        var pages = Math.Min(Math.Min(wallet.BonusPages, Math.Max(0, max)), job.Price.Price.Sides);

        if (pages <= 0)
        {
            return (0, Money.Zero);
        }

        var perSide = Money.FromAzn(job.Price.Price.PricePerSide * job.Price.Price.PaperMultiplier);
        var value = perSide * pages;

        return (pages, value > payment.Gross ? payment.Gross : value);
    }

    /// <summary>
    /// An app payment the user may see: one opened for the app at a kiosk and
    /// not someone else's (a payment already taken by another user reads as missing).
    /// </summary>
    /// <summary>
    /// A kiosk's "pay with the app" QR is new usage of <c>mobile_payment_at_kiosk</c>:
    /// refused while the flag is off. A try already in progress or paid carries
    /// on, and orders paid in the app for a kiosk (Mobile channel) are not this flag.
    /// </summary>
    private async Task RequireKioskAppPaymentAsync(Payment payment, Guid userId, CancellationToken cancellationToken)
    {
        if (payment.Channel == PaymentChannel.Kiosk
            && !await _features.IsEnabledAsync(Features.FeatureKeys.MobilePaymentAtKiosk, payment.KioskId, userId, cancellationToken).ConfigureAwait(false))
        {
            throw new Features.FeatureDisabledException(Features.FeatureKeys.MobilePaymentAtKiosk);
        }
    }

    private async Task<Payment> AppPaymentAsync(Guid userId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await _payments.FindByIdAsync(paymentId, cancellationToken).ConfigureAwait(false);

        // Only a payment the kiosk opened for the app (a bank QR is paid in a
        // bank app), and only by the user who took it.
        var forApp = payment is { Purpose: PaymentPurpose.PrintJob }
                     && payment.QrContent?.StartsWith("neoprint://pay/", StringComparison.Ordinal) == true;

        return !forApp || payment!.UserId is { } owner && owner != userId
            ? throw new NotFoundException("payment")
            : payment!;
    }

    private async Task<PrintJob> JobOfAsync(Payment payment, CancellationToken cancellationToken)
        => payment.JobId is { } jobId
            ? await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("printJob")
            : throw new NotFoundException("printJob");

    private async Task<AppPaymentView> ViewAsync(Payment payment, Guid userId, CancellationToken cancellationToken)
    {
        var job = await JobOfAsync(payment, cancellationToken).ConfigureAwait(false);
        var kiosk = payment.KioskId is { } kioskId ? await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false) : null;
        var wallet = await _wallet.ReadAsync(userId, cancellationToken).ConfigureAwait(false);
        var (usable, value) = await BonusAsync(payment, job, wallet, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var open = payment.Status == PaymentStatus.Pending;
        var schedule = job.Source == PrintJobSource.Scheduled
            ? await _schedules.FindByJobIdAsync(job.Id, cancellationToken).ConfigureAwait(false)
            : null;
        Promos.PromoCheck? promo = null;
        Promos.PromoCheck? promoWithBonus = null;

        if (open && job.PromoCode is { } code)
        {
            // What the code takes off either way, so the app shows the price it will charge.
            promo = await _promos.CheckAsync(code, payment.KioskId, job.Price.ProfileId, payment.Gross, userId, cancellationToken).ConfigureAwait(false);
            promoWithBonus = await _promos
                .CheckAsync(code, payment.KioskId, job.Price.ProfileId, payment.Gross - value, userId, cancellationToken)
                .ConfigureAwait(false);
        }

        return new AppPaymentView(
            payment.Id,
            payment.Status.ToString(),
            (open ? payment.Gross : payment.Amount).Amount,
            payment.Amount.Currency,
            payment.ExpiresAt,
            open ? Math.Max(0, (int)Math.Ceiling((payment.ExpiresAt - now).TotalSeconds)) : 0,
            kiosk?.Code,
            kiosk?.Name,
            job.JobCode,
            job.LocalFileName,
            job.Price.Price.Pages,
            job.Options.Copies,
            job.Options.Color,
            job.Options.Duplex,
            job.Options.Paper,
            wallet.Balance.Amount,
            wallet.BonusPages,
            usable,
            value.Amount,
            payment.WasPaid ? payment.Method.ToString() : null,
            payment.PaidAt,
            open ? job.PromoCode : payment.PromoCode,
            open ? promo?.Discount.Amount ?? 0 : payment.Discount.Amount,
            open ? promoWithBonus?.Discount.Amount ?? 0 : payment.Discount.Amount,
            promo?.Refusal is { } refusal ? Localization.Messages.Write(refusal) : null,
            job.HeldForUser && job.Source != PrintJobSource.Scheduled,
            schedule?.ScheduledAt);
    }
}
