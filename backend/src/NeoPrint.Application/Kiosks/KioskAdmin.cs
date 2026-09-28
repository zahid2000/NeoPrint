using System.Text.Json;
using System.Text.RegularExpressions;

using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Auth;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.Devices;
using NeoPrint.Application.Pricing;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Kiosks;

// ---------------------------------------------------------------------------
// What the panel sees (plan section 8A.3: "Kiosklar", "Kiosk yarat/redaktə")
// ---------------------------------------------------------------------------

/// <summary>A paper counter, as the kiosk form edits it.</summary>
public sealed record PaperStockDto(int Capacity, int Current, DateTime? LastRefillAt);

/// <summary>
/// A limit with the value that actually applies (plan section 2.2).
/// </summary>
/// <param name="Override">The kiosk's own value, or null for "Default istifadə et".</param>
/// <param name="Default">The installation default from the settings.</param>
/// <param name="Effective"><c>Override ?? Default</c>.</param>
public sealed record KioskLimitDto(int? Override, int Default, int Effective);

/// <summary>A device that is or was bound to the kiosk (the "Cihaz" tab).</summary>
public sealed record KioskDeviceHistoryItem(
    Guid Id,
    string Status,
    string Platform,
    string? AppVersion,
    string? Hostname,
    DateTime? ActivatedAt,
    DateTime? RevokedAt,
    string? RevokeReason,
    DateTime LastSeenAt,
    bool IsActive);

/// <summary>Everything the kiosk edit form and the detail page show.</summary>
/// <remarks>The admin PIN is never returned - only whether one is set.</remarks>
public sealed record KioskDetail(
    Guid Id,
    string Code,
    string Name,
    string Status,
    double Lat,
    double Lng,
    string Address,
    string DefaultLanguage,
    int? QueueDisplayLimit,
    KioskLimitDto MaxFileSizeMb,
    KioskLimitDto MaxPages,
    Guid? PricingProfileId,
    string? PricingProfileName,
    string? FiscalTerminalId,
    IReadOnlyDictionary<string, PaperStockDto> Paper,
    bool HasAdminPin,
    Guid? ActiveDeviceId,
    string Platform,
    string? AppVersion,
    string? SelectedPrinter,
    DateTime? LastHeartbeatAt,
    DateTime ConfigurationChangedAt,
    DateTime CreatedAt,
    int Revision,
    DateTime? SafeModeSince = null);

/// <summary>A new kiosk, as the "Kiosk yarat" form sends it.</summary>
/// <param name="Code">Empty: the next <c>KSK-XXXX</c> is generated.</param>
public sealed record CreateKioskInput(
    string Name,
    string? Code,
    double Lat,
    double Lng,
    string Address,
    string? DefaultLanguage,
    int? QueueDisplayLimit,
    int? MaxFileSizeMb,
    int? MaxPages,
    Guid? PricingProfileId,
    string? FiscalTerminalId,
    int PaperCapacityA4,
    int PaperCapacityA3,
    string AdminPin);

/// <summary>The general part of the edit form (<c>kiosks.edit</c>).</summary>
public sealed record UpdateKioskInput(
    string Name,
    double Lat,
    double Lng,
    string Address,
    string? DefaultLanguage,
    int? QueueDisplayLimit);

// ---------------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------------

/// <summary>One kiosk, if the administrator's scope covers it.</summary>
public sealed record GetKioskQuery(Guid KioskId, Guid AdminUserId) : IRequest<KioskDetail>;

public sealed record ListKioskDevicesQuery(Guid KioskId, Guid AdminUserId)
    : IRequest<IReadOnlyList<KioskDeviceHistoryItem>>;

/// <summary>
/// Creates a kiosk without a device (plan section 8A.3: it starts
/// <c>NotActivated</c>, a device is bound later).
/// </summary>
public sealed record CreateKioskCommand(CreateKioskInput Input, Guid AdminUserId, string? Ip)
    : IRequest<KioskDetail>, ITransactionalRequest;

public sealed record UpdateKioskCommand(Guid KioskId, UpdateKioskInput Input, Guid AdminUserId, string? Ip)
    : IRequest<KioskDetail>, ITransactionalRequest;

/// <summary>File limits; null means "Default istifadə et" (plan section 2.2).</summary>
public sealed record SetKioskLimitsCommand(
    Guid KioskId, int? MaxFileSizeMb, int? MaxPages, Guid AdminUserId, string? Ip)
    : IRequest<KioskDetail>, ITransactionalRequest;

/// <summary>Null falls back to the default pricing profile.</summary>
public sealed record SetKioskPricingProfileCommand(Guid KioskId, Guid? PricingProfileId, Guid AdminUserId, string? Ip)
    : IRequest<KioskDetail>, ITransactionalRequest;

public sealed record SetKioskFiscalTerminalCommand(Guid KioskId, string? FiscalTerminalId, Guid AdminUserId, string? Ip)
    : IRequest<KioskDetail>, ITransactionalRequest;

/// <summary>Tray capacities in sheets (plan section 2.15).</summary>
public sealed record SetKioskPaperCapacityCommand(
    Guid KioskId, int CapacityA4, int CapacityA3, Guid AdminUserId, string? Ip)
    : IRequest<KioskDetail>, ITransactionalRequest;

/// <summary>Replaces the PIN that unlocks the hidden admin exit on the kiosk.</summary>
public sealed record SetKioskAdminPinCommand(Guid KioskId, string Pin, Guid AdminUserId, string? Ip)
    : IRequest<KioskDetail>, ITransactionalRequest;

/// <summary>
/// Deletes a kiosk that never had a device. One with a history is kept:
/// its jobs, payments and counters refer to it.
/// </summary>
public sealed record DeleteKioskCommand(Guid KioskId, Guid AdminUserId, string? Ip)
    : IRequest, ITransactionalRequest;

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

/// <summary>The rules every kiosk form shares.</summary>
internal static class KioskRules
{
    public const int MaxFileSizeMbLimit = 2048;
    public const int MaxPagesLimit = 10_000;
    public const int MaxPaperCapacity = 100_000;

    /// <summary>Plan section 8A.3: the queue shows 3 or 5 entries.</summary>
    public static readonly int[] QueueDisplayLimits = [3, 5];

    public static IEnumerable<ValidationError> General(
        string name, double lat, double lng, string address, string? language, int? queueDisplayLimit)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
        {
            yield return new ValidationError("name", "kiosks.nameRequiredMax");
        }

        if (string.IsNullOrWhiteSpace(address) || address.Trim().Length > 300)
        {
            yield return new ValidationError("address", "kiosks.addressRequiredMax");
        }

        if (lat is < -90 or > 90 || double.IsNaN(lat))
        {
            yield return new ValidationError("lat", "kiosks.latitudeRange");
        }

        if (lng is < -180 or > 180 || double.IsNaN(lng))
        {
            yield return new ValidationError("lng", "kiosks.longitudeRange");
        }

        if (language is not null && !AuthRules.SupportedLanguages.Contains(language))
        {
            yield return new ValidationError("defaultLanguage", "common.languageInvalid");
        }

        if (queueDisplayLimit is not null && !QueueDisplayLimits.Contains(queueDisplayLimit.Value))
        {
            yield return new ValidationError("queueDisplayLimit", "kiosks.queueLimitInvalid");
        }
    }

    public static IEnumerable<ValidationError> Limits(int? maxFileSizeMb, int? maxPages)
    {
        if (maxFileSizeMb is < 1 or > MaxFileSizeMbLimit)
        {
            yield return new ValidationError(
                "maxFileSizeMb", "files.maxSizeRange", ("max", MaxFileSizeMbLimit));
        }

        if (maxPages is < 1 or > MaxPagesLimit)
        {
            yield return new ValidationError(
                "maxPages", "files.maxPagesRange", ("max", MaxPagesLimit));
        }
    }

    public static IEnumerable<ValidationError> Capacity(int capacity, string field)
    {
        if (capacity is < 0 or > MaxPaperCapacity)
        {
            yield return new ValidationError(field, "kiosks.paperCapacityRange", ("max", MaxPaperCapacity));
        }
    }

    public static IEnumerable<ValidationError> Pin(string? pin)
    {
        var trimmed = pin?.Trim() ?? string.Empty;

        if (trimmed.Length < ActivateDeviceCommandValidator.MinimumPinLength || trimmed.Length > 12)
        {
            yield return new ValidationError(
                "adminPin",
                "kiosks.pinLength",
                ("min", ActivateDeviceCommandValidator.MinimumPinLength));
        }
        else if (!trimmed.All(char.IsAsciiDigit))
        {
            // The kiosk's on-screen keypad is numeric.
            yield return new ValidationError("adminPin", "kiosks.pinDigits");
        }
    }

    public static IEnumerable<ValidationError> FiscalTerminal(string? id)
    {
        if (id is not null && id.Trim().Length > 64)
        {
            yield return new ValidationError("fiscalTerminalId", "kiosks.fiscalTerminalTooLong");
        }
    }
}

public sealed class CreateKioskCommandValidator : IValidator<CreateKioskCommand>
{
    public IEnumerable<ValidationError> Validate(CreateKioskCommand request)
    {
        var input = request.Input;

        return KioskRules.General(
                input.Name, input.Lat, input.Lng, input.Address, input.DefaultLanguage, input.QueueDisplayLimit)
            .Concat(KioskRules.Limits(input.MaxFileSizeMb, input.MaxPages))
            .Concat(KioskRules.Capacity(input.PaperCapacityA4, "paperCapacityA4"))
            .Concat(KioskRules.Capacity(input.PaperCapacityA3, "paperCapacityA3"))
            .Concat(KioskRules.Pin(input.AdminPin))
            .Concat(KioskRules.FiscalTerminal(input.FiscalTerminalId))
            .Concat(string.IsNullOrWhiteSpace(input.Code) || Regex.IsMatch(input.Code, "^KSK-[0-9]{4}$")
                ? []
                : [new ValidationError("code", "kiosks.codeFormat")]);
    }
}

public sealed class UpdateKioskCommandValidator : IValidator<UpdateKioskCommand>
{
    public IEnumerable<ValidationError> Validate(UpdateKioskCommand request)
        => KioskRules.General(
            request.Input.Name,
            request.Input.Lat,
            request.Input.Lng,
            request.Input.Address,
            request.Input.DefaultLanguage,
            request.Input.QueueDisplayLimit);
}

public sealed class SetKioskLimitsCommandValidator : IValidator<SetKioskLimitsCommand>
{
    public IEnumerable<ValidationError> Validate(SetKioskLimitsCommand request)
        => KioskRules.Limits(request.MaxFileSizeMb, request.MaxPages);
}

public sealed class SetKioskPaperCapacityCommandValidator : IValidator<SetKioskPaperCapacityCommand>
{
    public IEnumerable<ValidationError> Validate(SetKioskPaperCapacityCommand request)
        => KioskRules.Capacity(request.CapacityA4, "capacityA4")
            .Concat(KioskRules.Capacity(request.CapacityA3, "capacityA3"));
}

public sealed class SetKioskAdminPinCommandValidator : IValidator<SetKioskAdminPinCommand>
{
    public IEnumerable<ValidationError> Validate(SetKioskAdminPinCommand request) => KioskRules.Pin(request.Pin);
}

public sealed class SetKioskFiscalTerminalCommandValidator : IValidator<SetKioskFiscalTerminalCommand>
{
    public IEnumerable<ValidationError> Validate(SetKioskFiscalTerminalCommand request)
        => KioskRules.FiscalTerminal(request.FiscalTerminalId);
}

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

/// <summary>
/// The kiosk pages of the admin panel (plan section 8A.3), with an audit entry
/// for every change.
/// </summary>
/// <remarks>
/// <para>
/// Every change bumps <see cref="Kiosk.ConfigurationChangedAt"/>: the kiosk
/// learns from its next heartbeat that it should fetch its configuration
/// again (plan section 2.2: limits, pricing and paper reach the kiosk through
/// <c>kiosks/me/config</c>).
/// </para>
/// <para>
/// A scoped operator only reaches kiosks in their scope (plan section 8A.2);
/// one outside it is reported as not found rather than forbidden, so the
/// answer does not confirm it exists.
/// </para>
/// </remarks>
public sealed class KioskAdminHandlers :
    IRequestHandler<GetKioskQuery, KioskDetail>,
    IRequestHandler<ListKioskDevicesQuery, IReadOnlyList<KioskDeviceHistoryItem>>,
    IRequestHandler<CreateKioskCommand, KioskDetail>,
    IRequestHandler<UpdateKioskCommand, KioskDetail>,
    IRequestHandler<SetKioskLimitsCommand, KioskDetail>,
    IRequestHandler<SetKioskPricingProfileCommand, KioskDetail>,
    IRequestHandler<SetKioskFiscalTerminalCommand, KioskDetail>,
    IRequestHandler<SetKioskPaperCapacityCommand, KioskDetail>,
    IRequestHandler<SetKioskAdminPinCommand, KioskDetail>,
    IRequestHandler<DeleteKioskCommand>
{
    private readonly IKioskRepository _kiosks;
    private readonly IKioskDeviceRepository _devices;
    private readonly IPricingProfileRepository _profiles;
    private readonly IPermissionResolver _permissions;
    private readonly ISettingsReader _settings;
    private readonly ISecretHasher _secrets;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public KioskAdminHandlers(
        IKioskRepository kiosks,
        IKioskDeviceRepository devices,
        IPricingProfileRepository profiles,
        IPermissionResolver permissions,
        ISettingsReader settings,
        ISecretHasher secrets,
        IAuditWriter audit,
        IClock clock)
    {
        _kiosks = kiosks;
        _devices = devices;
        _profiles = profiles;
        _permissions = permissions;
        _settings = settings;
        _secrets = secrets;
        _audit = audit;
        _clock = clock;
    }

    // -----------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------

    public async Task<KioskDetail> Handle(GetKioskQuery request, CancellationToken cancellationToken)
        => await DetailAsync(
                await LoadAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<KioskDeviceHistoryItem>> Handle(
        ListKioskDevicesQuery request, CancellationToken cancellationToken)
    {
        var kiosk = await LoadAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);
        var devices = await _devices.ListForKioskAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        return [.. devices.Select(device => new KioskDeviceHistoryItem(
            device.Id,
            device.Status.ToString(),
            device.Platform.ToString(),
            device.AppVersion,
            device.Fingerprint.Hostname,
            device.ActivatedAt,
            device.RevokedAt,
            device.RevokeReason,
            device.LastSeenAt,
            device.Id == kiosk.ActiveDeviceId))];
    }

    // -----------------------------------------------------------------------
    // Create, update, delete
    // -----------------------------------------------------------------------

    public async Task<KioskDetail> Handle(CreateKioskCommand request, CancellationToken cancellationToken)
    {
        var input = request.Input;
        var code = string.IsNullOrWhiteSpace(input.Code) ? null : input.Code.Trim();

        if (code is null)
        {
            var highest = await _kiosks.GetHighestCodeSequenceAsync(cancellationToken).ConfigureAwait(false);
            code = Kiosk.NextCode(highest + 1);
        }
        else if (await _kiosks.FindByCodeAsync(code, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw Invalid("code", "kiosks.codeTaken", ("code", code));
        }

        await EnsureProfileExistsAsync(input.PricingProfileId, cancellationToken).ConfigureAwait(false);

        var now = _clock.UtcNow;
        var kiosk = new Kiosk
        {
            Code = code,
            Name = input.Name.Trim(),
            Location = new KioskLocation { Lat = input.Lat, Lng = input.Lng, Address = input.Address.Trim() },
            DefaultLanguage = input.DefaultLanguage ?? "az",
            QueueDisplayLimit = input.QueueDisplayLimit,
            MaxFileSizeMb = input.MaxFileSizeMb,
            MaxPages = input.MaxPages,
            PricingProfileId = input.PricingProfileId,
            FiscalTerminalId = Normalize(input.FiscalTerminalId),

            // Only the hash is stored, and only the hash ever reaches the
            // kiosk (plan sections 7.1, 2.13).
            AdminPinHash = _secrets.HashSecret(input.AdminPin.Trim()),
            CreatedAt = now,
            ConfigurationChangedAt = now,
        };

        kiosk.PaperStock["A4"] = new PaperStock { Capacity = input.PaperCapacityA4 };
        kiosk.PaperStock["A3"] = new PaperStock { Capacity = input.PaperCapacityA3 };

        await _kiosks.AddAsync(kiosk, cancellationToken).ConfigureAwait(false);

        await AuditAsync(request.AdminUserId, "kiosks.create", kiosk, null, Snapshot(kiosk), request.Ip, cancellationToken)
            .ConfigureAwait(false);

        return await DetailAsync(kiosk, cancellationToken).ConfigureAwait(false);
    }

    public Task<KioskDetail> Handle(UpdateKioskCommand request, CancellationToken cancellationToken)
        => ChangeAsync(
            request.KioskId,
            request.AdminUserId,
            request.Ip,
            "kiosks.edit",
            kiosk =>
            {
                kiosk.Name = request.Input.Name.Trim();
                kiosk.Location = new KioskLocation
                {
                    Lat = request.Input.Lat,
                    Lng = request.Input.Lng,
                    Address = request.Input.Address.Trim(),
                };
                kiosk.DefaultLanguage = request.Input.DefaultLanguage ?? kiosk.DefaultLanguage;
                kiosk.QueueDisplayLimit = request.Input.QueueDisplayLimit;
                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<KioskDetail> Handle(SetKioskLimitsCommand request, CancellationToken cancellationToken)
        => ChangeAsync(
            request.KioskId,
            request.AdminUserId,
            request.Ip,
            "kiosks.limits",
            kiosk =>
            {
                kiosk.MaxFileSizeMb = request.MaxFileSizeMb;
                kiosk.MaxPages = request.MaxPages;
                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<KioskDetail> Handle(SetKioskPricingProfileCommand request, CancellationToken cancellationToken)
        => ChangeAsync(
            request.KioskId,
            request.AdminUserId,
            request.Ip,
            "kiosks.pricing-profile",
            async kiosk =>
            {
                await EnsureProfileExistsAsync(request.PricingProfileId, cancellationToken).ConfigureAwait(false);
                kiosk.PricingProfileId = request.PricingProfileId;
            },
            cancellationToken);

    public Task<KioskDetail> Handle(SetKioskFiscalTerminalCommand request, CancellationToken cancellationToken)
        => ChangeAsync(
            request.KioskId,
            request.AdminUserId,
            request.Ip,
            "kiosks.fiscal-terminal",
            kiosk =>
            {
                kiosk.FiscalTerminalId = Normalize(request.FiscalTerminalId);
                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<KioskDetail> Handle(SetKioskPaperCapacityCommand request, CancellationToken cancellationToken)
        => ChangeAsync(
            request.KioskId,
            request.AdminUserId,
            request.Ip,
            "kiosks.paper-capacity",
            kiosk =>
            {
                // Only the capacity: what is loaded is counted by refills and
                // jobs (plan section 2.15), never typed into this form. A
                // counter above a lowered capacity is brought down to it.
                foreach (var (size, capacity) in new[] { ("A4", request.CapacityA4), ("A3", request.CapacityA3) })
                {
                    var stock = kiosk.PaperStock.TryGetValue(size, out var existing) ? existing : new PaperStock();
                    stock.Capacity = capacity;
                    stock.Current = Math.Min(stock.Current, capacity);
                    kiosk.PaperStock[size] = stock;
                }

                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<KioskDetail> Handle(SetKioskAdminPinCommand request, CancellationToken cancellationToken)
        => ChangeAsync(
            request.KioskId,
            request.AdminUserId,
            request.Ip,
            "kiosks.admin-pin",
            kiosk =>
            {
                kiosk.AdminPinHash = _secrets.HashSecret(request.Pin.Trim());
                return Task.CompletedTask;
            },
            cancellationToken);

    public async Task Handle(DeleteKioskCommand request, CancellationToken cancellationToken)
    {
        var kiosk = await LoadAsync(request.KioskId, request.AdminUserId, cancellationToken).ConfigureAwait(false);

        var everBound = kiosk.ActiveDeviceId is not null
                        || (await _devices.ListForKioskAsync(kiosk.Id, cancellationToken).ConfigureAwait(false)).Count > 0;

        if (kiosk.Status != KioskStatus.NotActivated || everBound)
        {
            throw Invalid(
                "kiosk",
                "kiosks.deleteHasHistory",
                ("code", kiosk.Code));
        }

        await _kiosks.DeleteAsync(kiosk.Id, cancellationToken).ConfigureAwait(false);

        await AuditAsync(request.AdminUserId, "kiosks.delete", kiosk, Snapshot(kiosk), null, request.Ip, cancellationToken)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<KioskDetail> ChangeAsync(
        Guid kioskId,
        Guid adminUserId,
        string? ip,
        string action,
        Func<Kiosk, Task> change,
        CancellationToken cancellationToken)
    {
        var kiosk = await LoadAsync(kioskId, adminUserId, cancellationToken).ConfigureAwait(false);
        var before = Snapshot(kiosk);

        await change(kiosk).ConfigureAwait(false);
        kiosk.ConfigurationChangedAt = _clock.UtcNow;

        await _kiosks.UpdateAsync(kiosk, cancellationToken).ConfigureAwait(false);

        await AuditAsync(adminUserId, action, kiosk, before, Snapshot(kiosk), ip, cancellationToken)
            .ConfigureAwait(false);

        return await DetailAsync(kiosk, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Kiosk> LoadAsync(Guid kioskId, Guid adminUserId, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);

        var kiosk = await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false);

        if (kiosk is null || !effective.CanSee(kiosk.Id))
        {
            throw new NotFoundException("kiosk");
        }

        return kiosk;
    }

    private async Task EnsureProfileExistsAsync(Guid? profileId, CancellationToken cancellationToken)
    {
        if (profileId is { } id && await _profiles.FindByIdAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw Invalid("pricingProfileId", "pricing.profileUnknown");
        }
    }

    private async Task<KioskDetail> DetailAsync(Kiosk kiosk, CancellationToken cancellationToken)
    {
        var defaultSize = await _settings.GetIntAsync(SettingKeys.FilesDefaultMaxSizeMb, 50, cancellationToken)
            .ConfigureAwait(false);
        var defaultPages = await _settings.GetIntAsync(SettingKeys.FilesDefaultMaxPages, 200, cancellationToken)
            .ConfigureAwait(false);

        var profile = kiosk.PricingProfileId is { } profileId
            ? await _profiles.FindByIdAsync(profileId, cancellationToken).ConfigureAwait(false)
            : await _profiles.FindDefaultAsync(cancellationToken).ConfigureAwait(false);

        return new KioskDetail(
            kiosk.Id,
            kiosk.Code,
            kiosk.Name,
            kiosk.Status.ToString(),
            kiosk.Location.Lat,
            kiosk.Location.Lng,
            kiosk.Location.Address,
            kiosk.DefaultLanguage,
            kiosk.QueueDisplayLimit,
            new KioskLimitDto(kiosk.MaxFileSizeMb, defaultSize, kiosk.MaxFileSizeMb ?? defaultSize),
            new KioskLimitDto(kiosk.MaxPages, defaultPages, kiosk.MaxPages ?? defaultPages),
            kiosk.PricingProfileId,
            profile?.Name,
            kiosk.FiscalTerminalId,
            kiosk.PaperStock.ToDictionary(
                pair => pair.Key,
                pair => new PaperStockDto(pair.Value.Capacity, pair.Value.Current, pair.Value.LastRefillAt),
                StringComparer.Ordinal),
            !string.IsNullOrEmpty(kiosk.AdminPinHash),
            kiosk.ActiveDeviceId,
            kiosk.Platform.ToString(),
            kiosk.AppVersion,
            kiosk.SelectedPrinter?.Name,
            kiosk.LastHeartbeatAt,
            kiosk.ConfigurationChangedAt,
            kiosk.CreatedAt,
            kiosk.RowVersion,
            kiosk.SafeModeSince);
    }

    /// <summary>What the audit log keeps: never the PIN hash.</summary>
    private static string Snapshot(Kiosk kiosk) => JsonSerializer.Serialize(new
    {
        kiosk.Code,
        kiosk.Name,
        kiosk.Location,
        kiosk.DefaultLanguage,
        kiosk.QueueDisplayLimit,
        kiosk.MaxFileSizeMb,
        kiosk.MaxPages,
        kiosk.PricingProfileId,
        kiosk.FiscalTerminalId,
        Paper = kiosk.PaperStock.ToDictionary(pair => pair.Key, pair => pair.Value.Capacity),
        HasAdminPin = kiosk.AdminPinHash is not null,
    });

    private Task AuditAsync(
        Guid adminUserId,
        string action,
        Kiosk kiosk,
        string? before,
        string? after,
        string? ip,
        CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(adminUserId, action, "Kiosk", kiosk.Id.ToString(), before, after, ip, Reason: null),
            cancellationToken);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ValidationException Invalid(string field, string key, params (string Name, object? Value)[] arguments)
        => new([new ValidationError(field, key, arguments)]);
}
