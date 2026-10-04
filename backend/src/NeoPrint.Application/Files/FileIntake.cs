using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Files;

/// <summary>
/// The checks every new file passes, whichever way it arrives - an upload, a
/// scan, a layout (plan section 2.2): size, quota, the default auto-delete
/// rule and a name that cannot be used as a path.
/// </summary>
public sealed class FileIntake
{
    private readonly IFileRepository _files;
    private readonly IKioskRepository _kiosks;
    private readonly ISettingsReader _settings;

    public FileIntake(IFileRepository files, IKioskRepository kiosks, ISettingsReader settings)
    {
        _files = files;
        _kiosks = kiosks;
        _settings = settings;
    }

    /// <summary>The largest file accepted: the kiosk's own limit when one is chosen, else the default.</summary>
    public async Task<long> MaxBytesAsync(Guid? kioskId, CancellationToken cancellationToken)
    {
        var defaultMb = await _settings
            .GetIntAsync(SettingKeys.FilesDefaultMaxSizeMb, 50, cancellationToken)
            .ConfigureAwait(false);

        if (kioskId is null)
        {
            return defaultMb * 1024L * 1024L;
        }

        // The kiosk's own limit wins when it has one (plan section 2.2).
        var kiosk = await _kiosks.FindByIdAsync(kioskId.Value, cancellationToken).ConfigureAwait(false);

        return (kiosk?.MaxFileSizeMb ?? defaultMb) * 1024L * 1024L;
    }

    /// <summary>Refuses a file of <paramref name="bytes"/> that would not fit.</summary>
    public static void EnsureWithin(long bytes, long maxBytes)
    {
        if (bytes > maxBytes)
        {
            throw Invalid("file", "files.tooLarge", ("size", Megabytes(bytes)), ("max", Megabytes(maxBytes)));
        }
    }

    /// <summary>Refuses <paramref name="incoming"/> more bytes when the user's quota cannot take them.</summary>
    public async Task EnsureQuotaAsync(Guid userId, long incoming, CancellationToken cancellationToken)
    {
        var quotaGb = await _settings
            .GetIntAsync(SettingKeys.FilesUserQuotaGb, 1, cancellationToken)
            .ConfigureAwait(false);

        var quota = quotaGb * 1024L * 1024L * 1024L;
        var used = await _files.TotalBytesAsync(userId, cancellationToken).ConfigureAwait(false);

        if (used + incoming > quota)
        {
            throw Invalid("file", "files.quotaFull", ("used", Megabytes(used)), ("quota", quotaGb));
        }
    }

    /// <summary>
    /// When a new file deletes itself: never, unless it is a personal document,
    /// which by default goes after its print (plan section 2.13,
    /// <c>Privacy.SensitiveAutoDeleteDefault</c>).
    /// </summary>
    public async Task<AutoDeleteRule> DefaultAutoDeleteAsync(bool isSensitive, CancellationToken cancellationToken)
    {
        if (!isSensitive)
        {
            return AutoDeleteRule.Never;
        }

        var configured = await _settings
            .GetAsync(SettingKeys.PrivacySensitiveAutoDeleteDefault, cancellationToken)
            .ConfigureAwait(false);

        return Enum.TryParse<AutoDeleteRule>(configured, ignoreCase: true, out var rule)
            ? rule
            : AutoDeleteRule.AfterPrint;
    }

    /// <summary>
    /// A display name that cannot be used as a path.
    /// </summary>
    /// <remarks>
    /// The object key is built from identifiers, so this is only what the user
    /// sees - but a name carrying <c>../</c> or a NUL byte would still end up
    /// in a Content-Disposition header and in the admin panel.
    /// </remarks>
    public static string SafeName(string? fileName, string extension)
    {
        var candidate = Path.GetFileName(fileName ?? string.Empty).Trim();

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(c, '_');
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"sənəd.{extension}";
        }

        return candidate.Length > 200 ? candidate[..200] : candidate;
    }

    /// <summary><see cref="SafeName"/>, ending in <paramref name="extension"/> whatever it was given.</summary>
    public static string SafeNameWithExtension(string? fileName, string fallbackStem, string extension)
    {
        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName ?? string.Empty)).Trim();

        return SafeName($"{(string.IsNullOrWhiteSpace(stem) ? fallbackStem : stem)}.{extension}", extension);
    }

    public static long Megabytes(long bytes) => bytes / (1024 * 1024);

    public static ValidationException Invalid(string field, string key, params (string Name, object? Value)[] arguments)
        => new([new ValidationError(field, key, arguments)]);
}
