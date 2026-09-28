using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Files;

// ---------------------------------------------------------------------------
// The "Fayl formatları" page (plan sections 2.2, 8A.3): which formats are
// accepted, and the default limits.
// ---------------------------------------------------------------------------

/// <summary>One catalogue entry and whether it is switched on.</summary>
/// <param name="Group">The plan's table row: Pdf, TextDocument, Spreadsheet, Presentation, Image, PageDescription.</param>
/// <param name="Route">How it becomes a PDF: None, LibreOffice, Ghostscript, Image.</param>
public sealed record FileFormatItem(
    string Extension, string MediaType, string Group, string Route, bool PrinterNative, bool Enabled);

public sealed record FileFormatsResponse(
    IReadOnlyList<FileFormatItem> Formats,
    int DefaultMaxSizeMb,
    int DefaultMaxPages,
    int UserQuotaGb);

public sealed record GetFileFormatsQuery : IRequest<FileFormatsResponse>;

/// <param name="Enabled">Extensions to accept; everything else in the catalogue is refused.</param>
public sealed record UpdateFileFormatsCommand(
    IReadOnlyList<string> Enabled,
    int DefaultMaxSizeMb,
    int DefaultMaxPages,
    int UserQuotaGb,
    Guid? UpdatedBy,
    string? Ip) : IRequest<FileFormatsResponse>, ITransactionalRequest;

public sealed class UpdateFileFormatsCommandValidator : IValidator<UpdateFileFormatsCommand>
{
    public const int MaxSizeMbLimit = 2048;
    public const int MaxPagesLimit = 10_000;
    public const int MaxQuotaGb = 1000;

    public IEnumerable<ValidationError> Validate(UpdateFileFormatsCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Enabled is null || request.Enabled.Count == 0)
        {
            // Nothing could be uploaded or printed at all, which is an outage,
            // not a configuration.
            yield return new ValidationError("enabled", "formats.oneRequired");
        }
        else
        {
            var unknown = request.Enabled
                .Where(extension => FileFormats.Find(extension) is null)
                .ToList();

            if (unknown.Count > 0)
            {
                // Adding a format means a converter that has to exist; the
                // catalogue is code, not configuration.
                yield return new ValidationError(
                    "enabled", "formats.notInCatalogue", ("formats", string.Join(", ", unknown)));
            }
        }

        if (request.DefaultMaxSizeMb is < 1 or > MaxSizeMbLimit)
        {
            yield return new ValidationError(
                "defaultMaxSizeMb", "files.maxSizeRange", ("max", MaxSizeMbLimit));
        }

        if (request.DefaultMaxPages is < 1 or > MaxPagesLimit)
        {
            yield return new ValidationError(
                "defaultMaxPages", "files.maxPagesRange", ("max", MaxPagesLimit));
        }

        if (request.UserQuotaGb is < 1 or > MaxQuotaGb)
        {
            yield return new ValidationError(
                "userQuotaGb", "formats.quotaRange", ("max", MaxQuotaGb));
        }
    }
}

/// <summary>
/// Reads and writes the format settings: <c>Files.AllowedFormats</c> and the
/// three default limits.
/// </summary>
/// <remarks>
/// A narrower door onto the same settings the "Tənzimləmələr" page edits, so
/// <c>formats.manage</c> can be granted without <c>settings.manage</c>. The
/// audit is one entry per changed setting, as there.
/// </remarks>
public sealed class FileFormatAdminHandlers :
    IRequestHandler<GetFileFormatsQuery, FileFormatsResponse>,
    IRequestHandler<UpdateFileFormatsCommand, FileFormatsResponse>
{
    private readonly ISettingsStore _store;
    private readonly ISettingsReader _settings;
    private readonly IAuditWriter _audit;

    public FileFormatAdminHandlers(ISettingsStore store, ISettingsReader settings, IAuditWriter audit)
    {
        _store = store;
        _settings = settings;
        _audit = audit;
    }

    public async Task<FileFormatsResponse> Handle(GetFileFormatsQuery request, CancellationToken cancellationToken)
    {
        var allowed = await _settings.GetAsync(SettingKeys.FilesAllowedFormats, cancellationToken).ConfigureAwait(false);

        // Empty means the catalogue default - every format - as upload reads it.
        var enabled = string.IsNullOrWhiteSpace(allowed)
            ? FileFormats.Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new FileFormatsResponse(
            [.. FileFormats.All
                .OrderBy(format => format.Group)
                .ThenBy(format => format.Extension, StringComparer.Ordinal)
                .Select(format => new FileFormatItem(
                    format.Extension,
                    format.MediaType,
                    format.Group.ToString(),
                    format.Route.ToString(),
                    format.PrinterNative,
                    enabled.Contains(format.Extension)))],
            await _settings.GetIntAsync(SettingKeys.FilesDefaultMaxSizeMb, 50, cancellationToken).ConfigureAwait(false),
            await _settings.GetIntAsync(SettingKeys.FilesDefaultMaxPages, 200, cancellationToken).ConfigureAwait(false),
            await _settings.GetIntAsync(SettingKeys.FilesUserQuotaGb, 1, cancellationToken).ConfigureAwait(false));
    }

    public async Task<FileFormatsResponse> Handle(UpdateFileFormatsCommand request, CancellationToken cancellationToken)
    {
        // Canonical and in catalogue order, so the stored value does not
        // change just because the panel sent the same set in another order.
        var enabled = request.Enabled
            .Select(extension => FileFormats.Find(extension)!.Extension)
            .ToHashSet(StringComparer.Ordinal);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SettingKeys.FilesAllowedFormats] = string.Join(
                ',', FileFormats.All.Select(format => format.Extension).Where(enabled.Contains)),
            [SettingKeys.FilesDefaultMaxSizeMb] = request.DefaultMaxSizeMb.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SettingKeys.FilesDefaultMaxPages] = request.DefaultMaxPages.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SettingKeys.FilesUserQuotaGb] = request.UserQuotaGb.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        var before = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(setting => values.ContainsKey(setting.Key))
            .ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal);

        var changed = values
            .Where(pair => !before.TryGetValue(pair.Key, out var old) || old != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        if (changed.Count > 0)
        {
            await _store.UpdateAsync(changed, request.UpdatedBy, cancellationToken).ConfigureAwait(false);

            foreach (var (key, value) in changed)
            {
                await _audit.WriteAsync(
                        new AuditEntry(
                            request.UpdatedBy,
                            "formats.update",
                            "AdminSetting",
                            key,
                            before.GetValueOrDefault(key),
                            value,
                            request.Ip,
                            Reason: null,
                            ActorType.Admin),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return await Handle(new GetFileFormatsQuery(), cancellationToken).ConfigureAwait(false);
    }
}
