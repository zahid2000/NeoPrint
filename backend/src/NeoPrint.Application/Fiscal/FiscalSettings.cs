using MediatR;

using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;

namespace NeoPrint.Application.Fiscal;

/// <summary>
/// The fiscal part of the settings (plan section 6: <c>GET/PUT admin/fiscal/settings</c>):
/// the online terminal, the top-up mode, the pending limit, on or off. Behind
/// <c>fiscal.manage</c>, so finance can change them without every setting.
/// </summary>
public sealed record GetFiscalSettingsQuery : IRequest<IReadOnlyList<SettingResponse>>;

public sealed record UpdateFiscalSettingsCommand(IReadOnlyDictionary<string, string> Values, Guid AdminUserId, string? Ip)
    : IRequest<IReadOnlyList<SettingResponse>>;

public sealed class FiscalSettingsHandlers :
    IRequestHandler<GetFiscalSettingsQuery, IReadOnlyList<SettingResponse>>,
    IRequestHandler<UpdateFiscalSettingsCommand, IReadOnlyList<SettingResponse>>
{
    private const string Prefix = "Fiscal.";

    private readonly ISender _sender;

    public FiscalSettingsHandlers(ISender sender) => _sender = sender;

    public async Task<IReadOnlyList<SettingResponse>> Handle(GetFiscalSettingsQuery request, CancellationToken cancellationToken)
        => Fiscal(await _sender.Send(new ListSettingsQuery(), cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<SettingResponse>> Handle(UpdateFiscalSettingsCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var other = request.Values.Keys.Where(key => !key.StartsWith(Prefix, StringComparison.Ordinal)).ToList();

        if (other.Count > 0)
        {
            throw new ValidationException([.. other.Select(key => new ValidationError(key, "fiscal.settingNotFiscal"))]);
        }

        // The general command checks the types, saves and audits.
        return Fiscal(await _sender.Send(new UpdateSettingsCommand(request.Values, request.AdminUserId, request.Ip), cancellationToken).ConfigureAwait(false));
    }

    private static IReadOnlyList<SettingResponse> Fiscal(IReadOnlyList<SettingResponse> all)
        => [.. all.Where(setting => setting.Key.StartsWith(Prefix, StringComparison.Ordinal))];
}
