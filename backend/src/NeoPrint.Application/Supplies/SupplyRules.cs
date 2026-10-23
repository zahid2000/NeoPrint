using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Supplies;

namespace NeoPrint.Application.Supplies;

// ---------------------------------------------------------------------------
// Rules: "Xəbərdarlıq qaydaları" (plan sections 2.15, 8A.3)
// ---------------------------------------------------------------------------

/// <summary>
/// The rules the account may see (T-295): one for every kiosk, and one that
/// names a kiosk in its scope - showing only the kiosks of its scope.
/// </summary>
public sealed record ListSupplyRulesQuery(Guid AdminUserId) : IRequest<IReadOnlyList<SupplyRuleView>>;

/// <param name="KioskIds">Empty: the rule is for every kiosk.</param>
/// <param name="Channels">Names of <see cref="AlertChannels"/>: <c>AdminPanel</c>, <c>OperatorPanel</c>, <c>Email</c>, <c>Telegram</c>.</param>
public sealed record SupplyRuleInput(
    string Name,
    IReadOnlyCollection<Guid>? KioskIds,
    string Resource,
    string? ResourceVariant,
    string Unit,
    decimal WarningThreshold,
    decimal CriticalThreshold,
    IReadOnlyCollection<string>? Channels,
    bool CreateServiceTask,
    string? TaskPriority,
    bool IsActive);

public sealed record CreateSupplyRuleCommand(Guid AdminUserId, SupplyRuleInput Rule, string? Ip) : IRequest<SupplyRuleView>;

public sealed record UpdateSupplyRuleCommand(Guid AdminUserId, Guid RuleId, SupplyRuleInput Rule, string? Ip) : IRequest<SupplyRuleView>;

public sealed record DeleteSupplyRuleCommand(Guid AdminUserId, Guid RuleId, string? Ip) : IRequest<Unit>;

// ---------------------------------------------------------------------------
// Alerts
// ---------------------------------------------------------------------------

/// <param name="Active">True: what is below its rule now (the default). False: what was. Null: both.</param>
public sealed record ListSupplyAlertsQuery(Guid AdminUserId, bool? Active, Guid? KioskId, int Page, int PageSize) : IRequest<SupplyAlertPage>;

public sealed record SupplyAlertPage(IReadOnlyList<SupplyAlertView> Items, long Total, int Page, int PageSize);

public sealed class SupplyRuleHandlers :
    IRequestHandler<ListSupplyRulesQuery, IReadOnlyList<SupplyRuleView>>,
    IRequestHandler<CreateSupplyRuleCommand, SupplyRuleView>,
    IRequestHandler<UpdateSupplyRuleCommand, SupplyRuleView>,
    IRequestHandler<DeleteSupplyRuleCommand, Unit>,
    IRequestHandler<ListSupplyAlertsQuery, SupplyAlertPage>
{
    private readonly ISupplyAlertRuleRepository _rules;
    private readonly ISupplyAlertRepository _alerts;
    private readonly IKioskRepository _kiosks;
    private readonly IPermissionResolver _permissions;
    private readonly IAuditWriter _audit;
    private readonly ISender _sender;
    private readonly IClock _clock;

    public SupplyRuleHandlers(
        ISupplyAlertRuleRepository rules,
        ISupplyAlertRepository alerts,
        IKioskRepository kiosks,
        IPermissionResolver permissions,
        IAuditWriter audit,
        ISender sender,
        IClock clock)
    {
        _rules = rules;
        _alerts = alerts;
        _kiosks = kiosks;
        _permissions = permissions;
        _audit = audit;
        _sender = sender;
        _clock = clock;
    }

    /// <inheritdoc cref="ListSupplyRulesQuery" />
    public async Task<IReadOnlyList<SupplyRuleView>> Handle(ListSupplyRulesQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);

        return [.. (await _rules.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(rule => effective.AllKiosks || rule.IsGlobal || rule.KioskIds.Any(effective.CanSee))
            .OrderBy(rule => rule.Resource)
            .ThenByDescending(rule => rule.IsGlobal)
            .ThenBy(rule => rule.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(SupplyRuleView.Of)
            .Select(view => effective.AllKiosks ? view : view with { KioskIds = [.. view.KioskIds.Where(effective.CanSee)] })];
    }

    /// <inheritdoc cref="CreateSupplyRuleCommand" />
    public async Task<SupplyRuleView> Handle(CreateSupplyRuleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await MayWriteAsync(request.AdminUserId, existing: null, request.Rule, cancellationToken).ConfigureAwait(false);

        var rule = new SupplyAlertRule { CreatedAt = _clock.UtcNow };

        await ApplyAsync(rule, request.Rule, cancellationToken).ConfigureAwait(false);
        await _rules.AddAsync(rule, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "supplyRules.create", rule, before: null, request.Ip, cancellationToken).ConfigureAwait(false);

        // A new rule may already be crossed somewhere; nobody should have to wait five minutes to hear.
        await _sender.Send(new EvaluateAllSuppliesCommand(), cancellationToken).ConfigureAwait(false);

        return SupplyRuleView.Of(rule);
    }

    /// <inheritdoc cref="UpdateSupplyRuleCommand" />
    public async Task<SupplyRuleView> Handle(UpdateSupplyRuleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = await _rules.FindByIdAsync(request.RuleId, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("supplyRule");
        await MayWriteAsync(request.AdminUserId, rule, request.Rule, cancellationToken).ConfigureAwait(false);
        var before = Describe(rule);

        await ApplyAsync(rule, request.Rule, cancellationToken).ConfigureAwait(false);
        await _rules.UpdateAsync(rule, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "supplyRules.update", rule, before, request.Ip, cancellationToken).ConfigureAwait(false);
        await _sender.Send(new EvaluateAllSuppliesCommand(), cancellationToken).ConfigureAwait(false);

        return SupplyRuleView.Of(rule);
    }

    /// <inheritdoc cref="DeleteSupplyRuleCommand" />
    public async Task<Unit> Handle(DeleteSupplyRuleCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = await _rules.FindByIdAsync(request.RuleId, cancellationToken).ConfigureAwait(false)
                   ?? throw new NotFoundException("supplyRule");
        await MayWriteAsync(request.AdminUserId, rule, input: null, cancellationToken).ConfigureAwait(false);

        await _rules.DeleteAsync(rule.Id, cancellationToken).ConfigureAwait(false);
        await _audit
            .WriteAsync(
                new AuditEntry(request.AdminUserId, "supplyRules.delete", nameof(SupplyAlertRule), rule.Id.ToString(), Describe(rule), null, request.Ip, null),
                cancellationToken)
            .ConfigureAwait(false);

        // Its alerts are judged by whatever rule is left, or cleared.
        await _sender.Send(new EvaluateAllSuppliesCommand(), cancellationToken).ConfigureAwait(false);

        return Unit.Value;
    }

    /// <inheritdoc cref="ListSupplyAlertsQuery" />
    public async Task<SupplyAlertPage> Handle(ListSupplyAlertsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var effective = await _permissions.ResolveAsync(request.AdminUserId, cancellationToken).ConfigureAwait(false);

        // A kiosk outside the account's scope is answered with nothing, like one that does not exist.
        if (request.KioskId is { } asked && !effective.CanSee(asked))
        {
            return new SupplyAlertPage([], 0, 1, Math.Clamp(request.PageSize, 1, 100));
        }

        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var (items, total) = await _alerts
            .ListAsync(request.Active, request.KioskId, effective.AllKiosks ? null : effective.KioskIds, (page - 1) * size, size, cancellationToken)
            .ConfigureAwait(false);
        var codes = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(kiosk => kiosk.Id, kiosk => kiosk.Code);

        return new SupplyAlertPage(
            [.. items.Select(alert => SupplyAlertView.Of(alert, codes.GetValueOrDefault(alert.KioskId)))], total, page, size);
    }

    /// <summary>
    /// A rule is written by an account that sees every kiosk it is for (T-295):
    /// one that looks after some kiosks writes rules for those, never one for
    /// every kiosk - that would set the alerts of kiosks it may not see.
    /// </summary>
    /// <exception cref="NotFoundException">The rule, or a kiosk it names, is not the account's to see.</exception>
    /// <exception cref="OutsideScopeException">The rule is also for a kiosk outside the account's scope.</exception>
    private async Task MayWriteAsync(Guid adminUserId, SupplyAlertRule? existing, SupplyRuleInput? input, CancellationToken cancellationToken)
    {
        var effective = await _permissions.ResolveAsync(adminUserId, cancellationToken).ConfigureAwait(false);
        if (effective.AllKiosks)
        {
            return;
        }

        if (existing is not null && !existing.IsGlobal && !existing.KioskIds.Any(effective.CanSee))
        {
            throw new NotFoundException("supplyRule");
        }

        if (input?.KioskIds is { Count: > 0 } kiosks && kiosks.Any(kioskId => !effective.CanSee(kioskId)))
        {
            throw new NotFoundException("kiosk");
        }

        if ((existing is not null && (existing.IsGlobal || !existing.KioskIds.All(effective.CanSee)))
            || (input is not null && input.KioskIds is not { Count: > 0 }))
        {
            throw new OutsideScopeException("supplyRules.outsideScope");
        }
    }

    /// <exception cref="NotFoundException">The rule names a kiosk there is not.</exception>
    private async Task ApplyAsync(SupplyAlertRule rule, SupplyRuleInput input, CancellationToken cancellationToken)
    {
        Shape(rule, input);

        if (rule.KioskIds.Count == 0)
        {
            return;
        }

        var known = (await _kiosks.ListAsync(cancellationToken).ConfigureAwait(false)).Select(kiosk => kiosk.Id).ToHashSet();

        if (rule.KioskIds.Any(id => !known.Contains(id)))
        {
            throw new NotFoundException("kiosk");
        }
    }

    /// <summary>
    /// Writes a request onto a rule and checks what came of it - without
    /// reading anything, so the validator asks the same question first.
    /// </summary>
    /// <exception cref="DomainException">A name the rule does not have, or a rule that could never fire.</exception>
    public static void Shape(SupplyAlertRule rule, SupplyRuleInput input)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(input);

        if (!Enum.TryParse<SupplyResource>(input.Resource, ignoreCase: false, out var resource) || !Enum.IsDefined(resource))
        {
            throw new DomainException("supplyRules.resourceUnknown");
        }

        if (!Enum.TryParse<SupplyUnit>(input.Unit, ignoreCase: false, out var unit) || !Enum.IsDefined(unit))
        {
            throw new DomainException("supplyRules.unitUnknown");
        }

        var priority = TaskPriority.Normal;

        if (!string.IsNullOrWhiteSpace(input.TaskPriority)
            && (!Enum.TryParse(input.TaskPriority, ignoreCase: false, out priority) || !Enum.IsDefined(priority)))
        {
            throw new DomainException("supplyRules.priorityUnknown");
        }

        var channels = AlertChannels.None;

        foreach (var name in input.Channels ?? [])
        {
            if (!Enum.TryParse<AlertChannels>(name, ignoreCase: false, out var channel) || !Enum.IsDefined(channel) || channel == AlertChannels.None)
            {
                throw new DomainException("supplyRules.channelUnknown");
            }

            channels |= channel;
        }

        rule.Name = (input.Name ?? string.Empty).Trim();
        rule.KioskIds = [.. (input.KioskIds ?? []).Distinct()];
        rule.Resource = resource;
        rule.ResourceVariant = string.IsNullOrWhiteSpace(input.ResourceVariant) ? null : input.ResourceVariant.Trim();
        rule.Unit = unit;
        rule.WarningThreshold = input.WarningThreshold;
        rule.CriticalThreshold = input.CriticalThreshold;
        rule.Channels = channels;
        rule.CreateServiceTask = input.CreateServiceTask;
        rule.TaskPriority = priority;
        rule.IsActive = input.IsActive;

        rule.EnsureValid();
    }

    private Task AuditAsync(Guid adminId, string action, SupplyAlertRule rule, string? before, string? ip, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(adminId, action, nameof(SupplyAlertRule), rule.Id.ToString(), before, Describe(rule), ip, null), cancellationToken);

    /// <summary>A rule in one line, for the audit log.</summary>
    private static string Describe(SupplyAlertRule rule)
        => FormattableString.Invariant(
            $"{rule.Name}: {rule.Resource}{(rule.ResourceVariant is null ? string.Empty : ":" + rule.ResourceVariant)} {rule.Unit} W{rule.WarningThreshold} C{rule.CriticalThreshold} kiosks={rule.KioskIds.Count} task={rule.CreateServiceTask} active={rule.IsActive}");
}
