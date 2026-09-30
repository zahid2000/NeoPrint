using System.Text.Json;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Promos;

namespace NeoPrint.Application.Promos;

/// <summary>
/// "Promokodu yoxla" (plan section 6: <c>POST promos/validate</c>): what the
/// code takes off an order of <paramref name="Amount"/> at a kiosk, or why it
/// does not apply. Nothing is held: the place is taken when the payment opens.
/// </summary>
public sealed record ValidatePromoQuery(string? Code, Guid? KioskId, decimal Amount, Guid? UserId) : IRequest<PromoValidation>;

/// <param name="Reason">Why it does not apply, in the caller's language.</param>
public sealed record PromoValidation(string Code, bool Valid, decimal Discount, decimal AmountDue, string? ReasonCode, string? Reason);

public sealed record ListPromosQuery(string? Code, bool? Active, int Page, int PageSize) : IRequest<PromoPage>;

public sealed record GetPromoQuery(Guid Id) : IRequest<PromoView>;

public sealed record SavePromoCommand(
    Guid AdminUserId,
    Guid? Id,
    string? Code,
    string? Kind,
    decimal Value,
    DateTime? StartsAt,
    DateTime? EndsAt,
    int? TotalLimit,
    int? PerUserLimit,
    decimal MinAmount,
    IReadOnlyList<Guid>? KioskIds,
    IReadOnlyList<Guid>? ProfileIds,
    string? Description,
    string? Ip)
    : IRequest<PromoView>, ITransactionalRequest;

public sealed record SetPromoActiveCommand(Guid AdminUserId, Guid Id, bool Active, string? Ip) : IRequest<PromoView>, ITransactionalRequest;

public sealed record PromoView(
    Guid Id,
    string Code,
    string Kind,
    decimal Value,
    DateTime? StartsAt,
    DateTime? EndsAt,
    int? TotalLimit,
    int? PerUserLimit,
    decimal MinAmount,
    IReadOnlyList<Guid> KioskIds,
    IReadOnlyList<Guid> ProfileIds,
    bool IsActive,
    int UsedCount,
    long ConfirmedCount,
    decimal TotalDiscount,
    string? Description,
    DateTime CreatedAt);

public sealed record PromoPage(IReadOnlyList<PromoView> Items, long Total, int Page, int PageSize);

public sealed class PromoHandlers :
    IRequestHandler<ValidatePromoQuery, PromoValidation>,
    IRequestHandler<ListPromosQuery, PromoPage>,
    IRequestHandler<GetPromoQuery, PromoView>,
    IRequestHandler<SavePromoCommand, PromoView>,
    IRequestHandler<SetPromoActiveCommand, PromoView>
{
    private readonly PromoService _service;
    private readonly IPromoCodeRepository _codes;
    private readonly IPromoUsageRepository _usages;
    private readonly IKioskRepository _kiosks;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public PromoHandlers(
        PromoService service, IPromoCodeRepository codes, IPromoUsageRepository usages, IKioskRepository kiosks, IAuditWriter audit, IClock clock)
    {
        _service = service;
        _codes = codes;
        _usages = usages;
        _kiosks = kiosks;
        _audit = audit;
        _clock = clock;
    }

    /// <inheritdoc cref="ValidatePromoQuery" />
    public async Task<PromoValidation> Handle(ValidatePromoQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Amount < 0 || decimal.Round(request.Amount, 2) != request.Amount)
        {
            throw new ValidationException([new ValidationError("amount", "promos.amountInvalid")]);
        }

        var kiosk = request.KioskId is { } kioskId ? await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false) : null;
        var amount = Money.FromAzn(request.Amount);
        var check = await _service
            .CheckAsync(request.Code ?? string.Empty, request.KioskId, kiosk?.PricingProfileId, amount, request.UserId, cancellationToken)
            .ConfigureAwait(false);

        return new PromoValidation(
            check.Promo?.Code ?? (request.Code ?? string.Empty).Trim().ToUpperInvariant(),
            check.Refusal is null,
            check.Discount.Amount,
            (amount - check.Discount).Amount,
            check.Refusal?.Key,
            check.Refusal is { } refusal ? Messages.Write(refusal) : null);
    }

    /// <inheritdoc cref="ListPromosQuery" />
    public async Task<PromoPage> Handle(ListPromosQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var (items, total) = await _codes.SearchAsync(request.Code, request.Active, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);
        var views = new List<PromoView>(items.Count);

        foreach (var promo in items)
        {
            views.Add(await ViewAsync(promo, cancellationToken).ConfigureAwait(false));
        }

        return new PromoPage(views, total, page, size);
    }

    /// <inheritdoc cref="GetPromoQuery" />
    public async Task<PromoView> Handle(GetPromoQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await ViewAsync(
                await _codes.FindByIdAsync(request.Id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("promoCode"),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="SavePromoCommand" />
    public async Task<PromoView> Handle(SavePromoCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<PromoKind>(request.Kind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            throw new ValidationException([new ValidationError("kind", "promos.kindInvalid")]);
        }

        PromoCode promo;
        string? before = null;

        if (request.Id is { } id)
        {
            promo = await _codes.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("promoCode");
            before = Snapshot(promo);
            promo.Change(
                kind, request.Value, request.StartsAt, request.EndsAt, request.TotalLimit, request.PerUserLimit, request.MinAmount,
                request.KioskIds, request.ProfileIds, request.Description);
            await _codes.UpdateAsync(promo, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            promo = PromoCode.Create(
                request.Code ?? string.Empty, kind, request.Value, request.StartsAt, request.EndsAt, request.TotalLimit, request.PerUserLimit,
                request.MinAmount, request.KioskIds, request.ProfileIds, request.Description, _clock.UtcNow);

            if (!await _codes.TryAddAsync(promo, cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(UserMessage.Of("promos.codeTaken", ("code", promo.Code)));
            }
        }

        await _audit.WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    request.Id is null ? "promos.create" : "promos.update",
                    nameof(PromoCode),
                    promo.Id.ToString(),
                    before,
                    Snapshot(promo),
                    request.Ip,
                    null),
                cancellationToken)
            .ConfigureAwait(false);

        return await ViewAsync(promo, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="SetPromoActiveCommand" />
    public async Task<PromoView> Handle(SetPromoActiveCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var promo = await _codes.FindByIdAsync(request.Id, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("promoCode");

        if (promo.IsActive != request.Active)
        {
            if (request.Active)
            {
                promo.Activate();
            }
            else
            {
                promo.Deactivate();
            }

            await _codes.UpdateAsync(promo, cancellationToken).ConfigureAwait(false);
            await _audit.WriteAsync(
                    new AuditEntry(
                        request.AdminUserId,
                        request.Active ? "promos.activate" : "promos.deactivate",
                        nameof(PromoCode),
                        promo.Id.ToString(),
                        null,
                        null,
                        request.Ip,
                        null),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await ViewAsync(promo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PromoView> ViewAsync(PromoCode promo, CancellationToken cancellationToken)
    {
        var (count, discount) = await _usages.StatsAsync(promo.Id, cancellationToken).ConfigureAwait(false);

        return new PromoView(
            promo.Id,
            promo.Code,
            promo.Kind.ToString(),
            promo.Value,
            promo.StartsAt,
            promo.EndsAt,
            promo.TotalLimit,
            promo.PerUserLimit,
            promo.MinAmount,
            promo.KioskIds,
            promo.ProfileIds,
            promo.IsActive,
            promo.UsedCount,
            count,
            discount,
            promo.Description,
            promo.CreatedAt);
    }

    private static string Snapshot(PromoCode promo)
        => JsonSerializer.Serialize(new
        {
            promo.Code,
            Kind = promo.Kind.ToString(),
            promo.Value,
            promo.StartsAt,
            promo.EndsAt,
            promo.TotalLimit,
            promo.PerUserLimit,
            promo.MinAmount,
            promo.KioskIds,
            promo.ProfileIds,
            promo.IsActive,
        });
}
