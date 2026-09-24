using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Admin;

/// <inheritdoc cref="ListSettingsQuery" />
public sealed class ListSettingsHandler
    : IRequestHandler<ListSettingsQuery, IReadOnlyList<SettingResponse>>
{
    private readonly ISettingsStore _settings;

    public ListSettingsHandler(ISettingsStore settings) => _settings = settings;

    public async Task<IReadOnlyList<SettingResponse>> Handle(
        ListSettingsQuery request, CancellationToken cancellationToken)
        => SettingsMapping.ToResponses(
            await _settings.ListAsync(cancellationToken).ConfigureAwait(false));
}

/// <inheritdoc cref="UpdateSettingsCommand" />
public sealed class UpdateSettingsHandler
    : IRequestHandler<UpdateSettingsCommand, IReadOnlyList<SettingResponse>>
{
    private readonly ISettingsStore _settings;
    private readonly IAuditWriter _audit;

    public UpdateSettingsHandler(ISettingsStore settings, IAuditWriter audit)
    {
        _settings = settings;
        _audit = audit;
    }

    public async Task<IReadOnlyList<SettingResponse>> Handle(
        UpdateSettingsCommand request, CancellationToken cancellationToken)
    {
        var before = await _settings.ListAsync(cancellationToken).ConfigureAwait(false);

        var changed = before
            .Where(setting => request.Values.ContainsKey(setting.Key))
            .ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal);

        var after = await _settings
            .UpdateAsync(request.Values, request.UpdatedBy, cancellationToken)
            .ConfigureAwait(false);

        // One entry per setting, so the audit log answers "who changed the
        // payment timeout" rather than "somebody saved the settings page".
        foreach (var (key, oldValue) in changed)
        {
            await _audit
                .WriteAsync(
                    new AuditEntry(
                        request.UpdatedBy,
                        "settings.update",
                        "AdminSetting",
                        key,
                        oldValue,
                        request.Values[key],
                        request.Ip,
                        Reason: null,
                        Domain.Common.ActorType.Admin),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return SettingsMapping.ToResponses(after);
    }
}

/// <summary>Shared mapping for the settings handlers.</summary>
internal static class SettingsMapping
{
    internal static IReadOnlyList<SettingResponse> ToResponses(IReadOnlyList<AdminSetting> settings)
    {
        var catalogue = SettingKeys.Defaults.ToDictionary(
            definition => definition.Key, StringComparer.Ordinal);

        return settings
            .Select(setting => new SettingResponse(
                setting.Key,
                setting.Value,
                setting.DefaultValue,
                setting.ValueType,
                // The stored description can be out of date if the catalogue
                // text was improved; the catalogue is the source.
                catalogue.TryGetValue(setting.Key, out var definition)
                    ? definition.Description
                    : setting.Description ?? string.Empty,
                setting.IsDefault,
                setting.IsDefault ? null : setting.UpdatedAt,
                setting.UpdatedBy))
            .ToList();
    }
}
