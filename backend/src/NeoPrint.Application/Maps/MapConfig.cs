using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Maps;

/// <summary>
/// What a map needs before it draws anything (<c>GET map/config</c>, T-217):
/// the style address and the attribution - the two settings, and nothing else.
/// </summary>
public sealed record GetMapConfigQuery : IRequest<MapConfigView>;

/// <param name="StyleUrl">A MapLibre style (OpenFreeMap by default).</param>
/// <param name="Attribution">Shown under every map, each as a link.</param>
public sealed record MapConfigView(string StyleUrl, IReadOnlyList<MapAttributionLink> Attribution);

public sealed class GetMapConfigHandler : IRequestHandler<GetMapConfigQuery, MapConfigView>
{
    private readonly ISettingsReader _settings;

    public GetMapConfigHandler(ISettingsReader settings) => _settings = settings;

    /// <remarks>
    /// A stored value the validator would refuse (written straight to the
    /// database) falls back to the default rather than handing the apps a map
    /// they cannot load or one without its licence.
    /// </remarks>
    public async Task<MapConfigView> Handle(GetMapConfigQuery request, CancellationToken cancellationToken)
    {
        var style = await _settings.GetAsync(SettingKeys.MapStyleUrl, cancellationToken).ConfigureAwait(false);
        var attribution = await _settings.GetAsync(SettingKeys.MapAttribution, cancellationToken).ConfigureAwait(false);

        return new MapConfigView(
            MapSettings.IsValidStyleUrl(style) ? style!.Trim() : MapSettings.DefaultStyleUrl,
            MapSettings.ParseAttribution(attribution) ?? MapSettings.ParseAttribution(MapSettings.DefaultAttribution)!);
    }
}
