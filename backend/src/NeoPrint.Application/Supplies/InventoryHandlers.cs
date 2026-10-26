using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Supplies;

// ---------------------------------------------------------------------------
// "Anbar" (plan sections 2.15, 8A.3)
// ---------------------------------------------------------------------------

/// <param name="PrinterModel">Only what fits this printer - "Toner dəyişdirildi" offers these.</param>
/// <param name="Type">A name of <see cref="InventoryItemType"/>, or null for every type.</param>
public sealed record ListInventoryItemsQuery(bool IncludeInactive, string? PrinterModel, string? Type) : IRequest<IReadOnlyList<InventoryItemView>>;

public sealed record InventoryItemInput(
    string Sku,
    string Name,
    string Type,
    IReadOnlyCollection<string>? CompatiblePrinterModels,
    string Unit,
    int? SheetsPerUnit,
    decimal MinStock,
    string? Location,
    bool IsActive,
    decimal? InitialStock = null);

/// <summary>A new item; what is already on the shelf (<c>InitialStock</c>) comes in as its first purchase (T-323).</summary>
public sealed record CreateInventoryItemCommand(Guid AdminUserId, InventoryItemInput Item, string? Ip) : IRequest<InventoryItemView>, ITransactionalRequest;

public sealed record UpdateInventoryItemCommand(Guid AdminUserId, Guid ItemId, InventoryItemInput Item, string? Ip) : IRequest<InventoryItemView>;

/// <summary>A purchase, a hand-out, a write-off, a recount: one movement of an item's count.</summary>
/// <param name="Quantity">Units, positive; for an <c>Adjustment</c> the sign is the direction.</param>
public sealed record RecordInventoryMovementCommand(
    Guid AdminUserId, Guid ItemId, string Type, decimal Quantity, Guid? KioskId, string? Note, string? Ip)
    : IRequest<InventoryItemView>, ITransactionalRequest;

/// <summary>The movements the account may see (T-295): those of no kiosk and of the kiosks in its scope.</summary>
public sealed record ListInventoryMovementsQuery(Guid AdminUserId, Guid? ItemId, Guid? KioskId, int Page, int PageSize) : IRequest<InventoryMovementPage>;

public sealed record InventoryMovementPage(IReadOnlyList<InventoryMovementView> Items, long Total, int Page, int PageSize);

/// <summary>
/// <c>InventoryLowStockJob</c> (plan section 6, hourly): an item below its
/// minimum is put before the administrators; one that is back above it is
/// taken away again.
/// </summary>
/// <returns>How many items are low.</returns>
public sealed record CheckInventoryLowStockCommand : IRequest<int>;

public sealed class InventoryHandlers :
    IRequestHandler<ListInventoryItemsQuery, IReadOnlyList<InventoryItemView>>,
    IRequestHandler<CreateInventoryItemCommand, InventoryItemView>,
    IRequestHandler<UpdateInventoryItemCommand, InventoryItemView>,
    IRequestHandler<RecordInventoryMovementCommand, InventoryItemView>,
    IRequestHandler<ListInventoryMovementsQuery, InventoryMovementPage>,
    IRequestHandler<CheckInventoryLowStockCommand, int>
{
    private readonly IInventoryRepository _inventory;
    private readonly IKioskRepository _kiosks;
    private readonly IPermissionResolver _permissions;
    private readonly IncidentReporter _incidents;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public InventoryHandlers(
        IInventoryRepository inventory,
        IKioskRepository kiosks,
        IPermissionResolver permissions,
        IncidentReporter incidents,
        IAuditWriter audit,
        IClock clock)
    {
        _inventory = inventory;
        _kiosks = kiosks;
        _permissions = permissions;
        _incidents = incidents;
        _audit = audit;
        _clock = clock;
    }

    public static string LowStockKey(Guid itemId) => $"inventory-low:{itemId:N}";

    /// <summary>The note of the purchase an item is made with: a code the panel writes out (T-323).</summary>
    public const string InitialStockNote = "inventory.initialStock";

    /// <inheritdoc cref="ListInventoryItemsQuery" />
    public async Task<IReadOnlyList<InventoryItemView>> Handle(ListInventoryItemsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        InventoryItemType? type = null;

        if (!string.IsNullOrWhiteSpace(request.Type))
        {
            type = Enum.TryParse<InventoryItemType>(request.Type, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new DomainException("inventory.typeUnknown");
        }

        return
        [
            .. (await _inventory.ListAsync(cancellationToken).ConfigureAwait(false))
                .Where(item => (request.IncludeInactive || item.IsActive)
                               && (type is null || item.Type == type)
                               && (string.IsNullOrWhiteSpace(request.PrinterModel) || item.Fits(request.PrinterModel)))

                // What is running out first, then by name.
                .OrderByDescending(item => item.IsLow)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(InventoryItemView.Of),
        ];
    }

    /// <inheritdoc cref="CreateInventoryItemCommand" />
    public async Task<InventoryItemView> Handle(CreateInventoryItemCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = new InventoryItem { CreatedAt = _clock.UtcNow };

        Shape(item, request.Item);

        if (await _inventory.FindBySkuAsync(item.Sku, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DomainException(UserMessage.Of("inventory.skuTaken", ("sku", item.Sku)));
        }

        // The count only moves through a movement, so what is already on the
        // shelf is the item's first purchase - in the movements and the audit
        // like any other (T-323).
        var initial = request.Item.InitialStock is > 0 and var stock
            ? item.Move(InventoryMovementType.Purchase, stock, request.AdminUserId, _clock.UtcNow, note: InitialStockNote)
            : null;

        await _inventory.AddAsync(item, cancellationToken).ConfigureAwait(false);
        await _audit
            .WriteAsync(new AuditEntry(request.AdminUserId, "inventory.items.create", nameof(InventoryItem), item.Id.ToString(), null, Describe(item), request.Ip, null), cancellationToken)
            .ConfigureAwait(false);

        if (initial is not null)
        {
            await _inventory.AddMovementAsync(initial, cancellationToken).ConfigureAwait(false);
            await _audit
                .WriteAsync(
                    new AuditEntry(
                        request.AdminUserId,
                        "inventory.movements.create",
                        nameof(InventoryItem),
                        item.Id.ToString(),
                        FormattableString.Invariant($"{item.Sku}: 0"),
                        FormattableString.Invariant($"{item.Sku}: {item.Stock} ({InventoryMovementType.Purchase} {initial.Quantity})"),
                        request.Ip,
                        initial.Note),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // An item made with less than its minimum is low from the start.
        await TellAsync(item, cancellationToken).ConfigureAwait(false);

        return InventoryItemView.Of(item);
    }

    /// <inheritdoc cref="UpdateInventoryItemCommand" />
    public async Task<InventoryItemView> Handle(UpdateInventoryItemCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = await _inventory.FindByIdAsync(request.ItemId, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("inventoryItem");
        var before = Describe(item);

        Shape(item, request.Item);
        item.NoteMinimum(_clock.UtcNow);

        if (await _inventory.FindBySkuAsync(item.Sku, cancellationToken).ConfigureAwait(false) is { } other && other.Id != item.Id)
        {
            throw new DomainException(UserMessage.Of("inventory.skuTaken", ("sku", item.Sku)));
        }

        await _inventory.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
        await _audit
            .WriteAsync(new AuditEntry(request.AdminUserId, "inventory.items.update", nameof(InventoryItem), item.Id.ToString(), before, Describe(item), request.Ip, null), cancellationToken)
            .ConfigureAwait(false);

        // A new minimum can put the item below it, or above.
        await TellAsync(item, cancellationToken).ConfigureAwait(false);

        return InventoryItemView.Of(item);
    }

    /// <inheritdoc cref="RecordInventoryMovementCommand" />
    public async Task<InventoryItemView> Handle(RecordInventoryMovementCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = await _inventory.FindByIdAsync(request.ItemId, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("inventoryItem");

        if (!Enum.TryParse<InventoryMovementType>(request.Type, ignoreCase: false, out var type) || !Enum.IsDefined(type))
        {
            throw new DomainException("inventory.movementTypeUnknown");
        }

        // A kiosk out of the account's scope is one it does not know of (T-295).
        if (request.KioskId is { } kioskId
            && (await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false) is null
                || !(await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false)).CanSee(kioskId)))
        {
            throw new NotFoundException("kiosk");
        }

        if (request.Note is { Length: > ServiceTaskHandlers.MaxNoteLength })
        {
            throw new DomainException(UserMessage.Of("serviceTasks.noteTooLong", ("max", ServiceTaskHandlers.MaxNoteLength)));
        }

        var before = item.Stock;
        var movement = item.Move(type, request.Quantity, request.AdminUserId, _clock.UtcNow, request.KioskId, taskId: null, request.Note);

        await _inventory.UpdateAsync(item, cancellationToken).ConfigureAwait(false);
        await _inventory.AddMovementAsync(movement, cancellationToken).ConfigureAwait(false);
        await _audit
            .WriteAsync(
                new AuditEntry(
                    request.AdminUserId,
                    "inventory.movements.create",
                    nameof(InventoryItem),
                    item.Id.ToString(),
                    FormattableString.Invariant($"{item.Sku}: {before}"),
                    FormattableString.Invariant($"{item.Sku}: {item.Stock} ({type} {movement.Quantity})"),
                    request.Ip,
                    movement.Note),
                cancellationToken)
            .ConfigureAwait(false);

        await TellAsync(item, cancellationToken).ConfigureAwait(false);

        return InventoryItemView.Of(item);
    }

    /// <inheritdoc cref="ListInventoryMovementsQuery" />
    public async Task<InventoryMovementPage> Handle(ListInventoryMovementsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);
        if (request.KioskId is { } asked && !effective.CanSee(asked))
        {
            throw new NotFoundException("kiosk");
        }

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var (items, total) = await _inventory
            .ListMovementsAsync(request.ItemId, request.KioskId, effective.AllKiosks ? null : effective.KioskIds, (page - 1) * size, size, cancellationToken)
            .ConfigureAwait(false);
        var names = (await _inventory.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(item => item.Id, item => item.Name);

        return new InventoryMovementPage(
            [
                .. items.Select(movement => new InventoryMovementView(
                    movement.Id,
                    movement.ItemId,
                    names.GetValueOrDefault(movement.ItemId),
                    movement.Type.ToString(),
                    movement.Quantity,
                    movement.StockAfter,
                    movement.KioskId,
                    movement.TaskId,
                    movement.By,
                    movement.At,
                    movement.Note is { } note && Messages.Has(note) ? Messages.Write(new UserMessage(note)) : movement.Note)),
            ],
            total,
            page,
            size);
    }

    /// <inheritdoc cref="CheckInventoryLowStockCommand" />
    public async Task<int> Handle(CheckInventoryLowStockCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var low = 0;

        foreach (var item in await _inventory.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await TellAsync(item, cancellationToken).ConfigureAwait(false))
            {
                low++;
            }
        }

        return low;
    }

    /// <summary>
    /// The incident of an item below its minimum: opened once while it lasts
    /// (the panel shows it, and it is e-mailed like any High incident), closed
    /// by the system when the count is back.
    /// </summary>
    /// <returns>Whether the item is low.</returns>
    public async Task<bool> TellAsync(InventoryItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!item.IsLow)
        {
            await _incidents
                .ResolveAsync(LowStockKey(item.Id), new UserMessage("incidents.resolution.inventoryRestocked"), cancellationToken)
                .ConfigureAwait(false);

            return false;
        }

        await _incidents
            .RaiseAsync(
                IncidentType.Other,
                IncidentSeverity.High,
                UserMessage.Of(
                    "incidents.inventory.low", ("item", item.Name), ("sku", item.Sku), ("stock", item.Stock), ("min", item.MinStock), ("unit", InventoryUnitNames.Normalize(item.Unit))),
                LowStockKey(item.Id),
                cancellationToken,
                kioskId: null,
                nameof(InventoryItem),
                item.Id)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>Writes a request onto an item and checks what came of it; reads nothing, so the validator asks the same first.</summary>
    /// <exception cref="DomainException">A field is missing or cannot be.</exception>
    public static void Shape(InventoryItem item, InventoryItemInput input)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(input);

        if (!Enum.TryParse<InventoryItemType>(input.Type, ignoreCase: false, out var type) || !Enum.IsDefined(type))
        {
            throw new DomainException("inventory.typeUnknown");
        }

        item.Sku = (input.Sku ?? string.Empty).Trim();
        item.Name = (input.Name ?? string.Empty).Trim();
        item.Type = type;
        item.CompatiblePrinterModels =
        [
            .. (input.CompatiblePrinterModels ?? [])
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        item.Unit = InventoryUnitNames.Normalize(input.Unit);
        item.SheetsPerUnit = input.SheetsPerUnit;
        item.MinStock = input.MinStock;
        item.Location = (input.Location ?? string.Empty).Trim();
        item.IsActive = input.IsActive;

        if (input.InitialStock is < 0)
        {
            throw new DomainException("inventory.initialStockInvalid");
        }

        item.EnsureValid();
    }

    private static string Describe(InventoryItem item)
        => FormattableString.Invariant($"{item.Sku} {item.Name} ({item.Type}) min={item.MinStock} stock={item.Stock} active={item.IsActive}");
}

/// <summary>
/// The code of a known warehouse unit, whatever it was written as (T-323):
/// "bağlama", "ream" and "пачка" are all <see cref="InventoryUnits.Ream"/>. The
/// words are the message catalog's own, so an item written before the units had
/// codes reads like a new one; any other word is the warehouse's own.
/// </summary>
public static class InventoryUnitNames
{
    private static readonly Lazy<Dictionary<string, string>> Words = new(() =>
    {
        var words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var code in InventoryUnits.Known)
        {
            words[code] = code;

            foreach (var language in Messages.Languages)
            {
                words[Messages.Write(new UserMessage($"inventory.unit.{code}"), language).TrimEnd('.')] = code;
            }
        }

        return words;
    });

    public static string Normalize(string? unit)
    {
        var trimmed = (unit ?? string.Empty).Trim();

        return Words.Value.TryGetValue(trimmed.TrimEnd('.'), out var code) ? code : trimmed;
    }
}
