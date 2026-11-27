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

                // "HH:mm-HH:mm", two different times; one may run past midnight (T-448).
                case SettingValueType.TimeRange when !IsTimeRange(value):
                    yield return new ValidationError(key, "settings.valueTimeRange");
                    break;

                default:
                    break;
            }

            // The two settings whose value is more than "a number" or "a word".
            if (key == SettingKeys.PrintJobMaxAutoRetries
                && int.TryParse(value, out var attempts)
                && attempts is < MinPrintAttempts or > MaxPrintAttempts)
            {
                yield return new ValidationError(key, "settings.valueRange", ("min", MinPrintAttempts), ("max", MaxPrintAttempts));
            }

            // A kiosk that sleeps after a minute of quiet would sleep between customers (T-448).
            if (key == SettingKeys.KioskSleepAfterIdleMinutes
                && int.TryParse(value, out var idle)
                && idle is < MinSleepIdleMinutes or > MaxSleepIdleMinutes)
            {
                yield return new ValidationError(key, "settings.valueRange", ("min", MinSleepIdleMinutes), ("max", MaxSleepIdleMinutes));
            }

            // The kiosk camera (T-482): two days by default, never past a month.
            if (CameraSettings.Ranges.TryGetValue(key, out var range)
                && int.TryParse(value, out var number)
                && (number < range.Min || number > range.Max))
            {
                yield return new ValidationError(key, "settings.valueRange", ("min", range.Min), ("max", range.Max));
            }

            if (key == SettingKeys.CameraMode && !CameraSettings.Modes.Contains(value))
            {
                yield return new ValidationError(key, "settings.cameraMode");
            }

            if (key == SettingKeys.CameraResolution && !CameraSettings.Resolutions.Contains(value))
            {
                yield return new ValidationError(key, "settings.cameraResolution");
            }

            if (key == SettingKeys.NotificationMinSeverity && !Severities.Contains(value, StringComparer.Ordinal))
            {
                yield return new ValidationError(key, "settings.valueSeverity");
            }

            // The map (T-217): a style the apps can load, and the data's licence always credited.
            if (key == SettingKeys.MapStyleUrl && !MapSettings.IsValidStyleUrl(value))
            {
                yield return new ValidationError(key, "settings.mapStyleUrl");
            }

            if (key == SettingKeys.MapAttribution && MapSettings.ParseAttribution(value) is null)
            {
                yield return new ValidationError(key, "settings.mapAttribution");
            }
        }
    }

    /// <summary>At least the first print; a printer that failed ten times will fail the eleventh.</summary>
    public const int MinPrintAttempts = 1;

    public const int MaxPrintAttempts = 10;

    /// <summary>The quiet a kiosk waits for before its night sleep, in minutes (T-448).</summary>
    public const int MinSleepIdleMinutes = 5;

    public const int MaxSleepIdleMinutes = 240;

    /// <summary>"HH:mm-HH:mm" with two different times.</summary>
    public static bool IsTimeRange(string value)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && TimeOnly.TryParseExact(parts[0], "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var from)
               && TimeOnly.TryParseExact(parts[1], "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var to)
               && from != to;
    }

    private static readonly string[] Severities = Enum.GetNames<NeoPrint.Domain.Incidents.IncidentSeverity>();
}

/// <summary>The kiosk camera's settings and their limits (T-482).</summary>
public static class CameraSettings
{
    /// <summary>Only while somebody is at the kiosk, a minute either side.</summary>
    public const string Activity = "Activity";

    public const string Continuous = "Continuous";

    public const int MinRetentionDays = 1;

    /// <summary>The owner's limit: a month at most.</summary>
    public const int MaxRetentionDays = 31;

    public const int MinDiskGb = 1;

    public const int MaxDiskGb = 500;

    public const int MinFps = 2;

    public const int MaxFps = 30;

    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal) { Activity, Continuous };

    public static readonly IReadOnlySet<string> Resolutions = new HashSet<string>(StringComparer.Ordinal) { "640x480", "1280x720", "1920x1080" };

    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Ranges = new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal)
    {
        [SettingKeys.CameraRetentionDays] = (MinRetentionDays, MaxRetentionDays),
        [SettingKeys.CameraMaxDiskGb] = (MinDiskGb, MaxDiskGb),
        [SettingKeys.CameraFps] = (MinFps, MaxFps),
    };
}
