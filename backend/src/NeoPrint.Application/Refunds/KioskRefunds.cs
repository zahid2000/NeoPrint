using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Refunds;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Refunds;

/// <summary>
/// What goes back for one of this kiosk's jobs (plan section 7.3, RefundInfo):
/// for the screen after a printer error or a cancellation - the amount, where
/// it goes, and the QR to the status page. Money only; bonus pages are not shown.
/// </summary>
public sealed record GetKioskJobRefundsQuery(Guid DeviceId, Guid JobId) : IRequest<IReadOnlyList<KioskRefundView>>;

/// <param name="Destination">Card, Wallet or Voucher.</param>
/// <param name="StatusUrl">The public status page the QR opens (plan section 2.9).</param>
public sealed record KioskRefundView(Guid Id, decimal Amount, string Currency, string Status, string Destination, string StatusUrl, string? VoucherCode);

public sealed class KioskRefundHandlers : IRequestHandler<GetKioskJobRefundsQuery, IReadOnlyList<KioskRefundView>>
{
    private readonly IKioskDeviceRepository _devices;
    private readonly IPrintJobRepository _jobs;
    private readonly IRefundRepository _refunds;
    private readonly ISettingsReader _settings;

    public KioskRefundHandlers(IKioskDeviceRepository devices, IPrintJobRepository jobs, IRefundRepository refunds, ISettingsReader settings)
    {
        _devices = devices;
        _jobs = jobs;
        _refunds = refunds;
        _settings = settings;
    }

    public async Task<IReadOnlyList<KioskRefundView>> Handle(GetKioskJobRefundsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (device?.KioskId is not { } kioskId || job is null || job.KioskId != kioskId)
        {
            throw new NotFoundException("printJob");
        }

        var site = await StatusPage.BaseAsync(_settings, cancellationToken).ConfigureAwait(false);
        var refunds = await _refunds.ListForJobAsync(job.Id, cancellationToken).ConfigureAwait(false);

        return
        [
            .. refunds
                .Where(refund => refund.Amount.IsPositive)
                .Select(refund => new KioskRefundView(
                    refund.Id,
                    refund.Amount.Amount,
                    refund.Amount.Currency,
                    refund.Status.ToString(),
                    PublicDestination(refund.Destination),
                    StatusPage.For(site, refund.Id),
                    refund.VoucherCode)),
        ];
    }

    public static string PublicDestination(RefundDestination destination) => destination switch
    {
        RefundDestination.Wallet => "Wallet",
        RefundDestination.Voucher => "Voucher",
        RefundDestination.Bonus => "Bonus",
        _ => "Card",
    };
}

/// <summary>The public refund status page's address (plan section 2.9: <c>{site}/refund/{id}</c>).</summary>
public static class StatusPage
{
    public static async Task<string> BaseAsync(ISettingsReader settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var site = await settings.GetAsync(SettingKeys.PublicSiteUrl, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(site) ? "https://neoprint.az" : site.Trim().TrimEnd('/');
    }

    public static string For(string site, Guid refundId) => $"{site}/refund/{refundId}";
}
