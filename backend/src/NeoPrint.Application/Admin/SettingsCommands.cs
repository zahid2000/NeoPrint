using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Admin;

/// <summary>
/// One platform setting as the panel edits it (plan section 2.4).
/// </summary>
/// <param name="IsDefault">
/// Whether it still holds the value it was seeded with, so the panel can show
/// what has been changed from the documented default.
/// </param>
public sealed record SettingResponse(
    string Key,
    string Value,
    string DefaultValue,
    SettingValueType ValueType,
    string Description,
    bool IsDefault,
    DateTime? UpdatedAt,
    Guid? UpdatedBy);

/// <summary>Every setting, with its documented default alongside.</summary>
public sealed record ListSettingsQuery : IRequest<IReadOnlyList<SettingResponse>>;

/// <summary>
/// Changes settings (plan section 2.4).
/// </summary>
/// <remarks>
/// Several at once, because the panel saves a page rather than a field, and a
/// half-applied page is worse than none: it runs in one transaction.
/// </remarks>
public sealed record UpdateSettingsCommand(
    IReadOnlyDictionary<string, string> Values,
    Guid? UpdatedBy,
    string? Ip) : IRequest<IReadOnlyList<SettingResponse>>, ITransactionalRequest;

/// <summary>
/// Checks the keys and the values before anything is written.
/// </summary>
/// <remarks>
/// An unknown key would sit in the collection forever with nothing reading it,
/// and a value of the wrong type would only fail later, at the point something
/// tries to use it.
/// </remarks>
public sealed class UpdateSettingsCommandValidator : IValidator<UpdateSettingsCommand>
{
    public IEnumerable<ValidationError> Validate(UpdateSettingsCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Values.Count == 0)
        {
            yield return new ValidationError("values", "settings.nothingToChange");
            yield break;
        }

        var catalogue = SettingKeys.Defaults.ToDictionary(setting => setting.Key, StringComparer.Ordinal);

        foreach (var (key, value) in request.Values)
        {
            if (!catalogue.TryGetValue(key, out var definition))
            {
                yield return new ValidationError(key, "settings.unknownKey", ("key", key));
                continue;
            }

            if (value is null)
            {
                yield return new ValidationError(key, "settings.valueEmpty");
                continue;
            }

            switch (definition.ValueType)
            {
                case SettingValueType.Integer when !int.TryParse(value, out _):
                    yield return new ValidationError(key, "settings.valueInteger");
                    break;

                case SettingValueType.Boolean when !bool.TryParse(value, out _):
                    yield return new ValidationError(key, "settings.valueBoolean");
                    break;

                case SettingValueType.Decimal when !decimal.TryParse(
                    value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out _):
                    yield return new ValidationError(key, "settings.valueDecimal");
                    break;

                default:
                    break;
            }
        }
    }
}
