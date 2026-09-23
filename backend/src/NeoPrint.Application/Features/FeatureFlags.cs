using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Features;

namespace NeoPrint.Application.Features;

/// <summary>Persistence for feature flags.</summary>
public interface IFeatureFlagRepository
{
    Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken cancellationToken = default);

    Task<FeatureFlag?> FindByKeyAsync(string key, CancellationToken cancellationToken = default);

    Task AddAsync(FeatureFlag flag, CancellationToken cancellationToken = default);

    Task UpdateAsync(FeatureFlag flag, CancellationToken cancellationToken = default);
}

/// <summary>
/// Evaluates feature flags (plan section 2.16).
/// </summary>
/// <remarks>
/// Evaluation happens on the backend and the answer is handed to each client,
/// so a flag can be turned off for one kiosk during the pilot without shipping
/// anything.
/// </remarks>
public interface IFeatureFlagEvaluator
{
    /// <summary>Whether a flag is on for a given kiosk or user.</summary>
    Task<bool> IsEnabledAsync(
        string key, Guid? kioskId = null, Guid? userId = null, CancellationToken cancellationToken = default);

    /// <summary>Every flag for a platform, evaluated for this kiosk or user.</summary>
    Task<IReadOnlyDictionary<string, bool>> EvaluateAllAsync(
        FeaturePlatform platform,
        Guid? kioskId = null,
        Guid? userId = null,
        CancellationToken cancellationToken = default);
}

// ---------------------------------------------------------------------------
// Queries and commands
// ---------------------------------------------------------------------------

/// <summary>The flags a mobile client should apply (<c>GET features</c>).</summary>
public sealed record GetMobileFeaturesQuery(Guid? UserId) : IRequest<IReadOnlyDictionary<string, bool>>;

/// <summary>Every flag with its configuration, for the admin panel.</summary>
public sealed record ListFeatureFlagsQuery : IRequest<IReadOnlyList<FeatureFlagDetails>>;

public sealed record FeatureFlagDetails(
    Guid Id,
    string Key,
    string? Description,
    bool DefaultEnabled,
    IReadOnlyCollection<FeaturePlatform> Platforms,
    IReadOnlyDictionary<string, bool> KioskOverrides,
    IReadOnlyCollection<Guid> UserAllowlist,
    int RolloutPercent,
    DateTime UpdatedAt);

/// <summary>Updates a flag.</summary>
public sealed record UpdateFeatureFlagCommand(
    string Key,
    bool DefaultEnabled,
    int RolloutPercent,
    IReadOnlyDictionary<string, bool>? KioskOverrides,
    IReadOnlyCollection<Guid>? UserAllowlist,
    Guid UpdatedBy,
    string? Ip) : IRequest<FeatureFlagDetails>;

public sealed class UpdateFeatureFlagCommandValidator : IValidator<UpdateFeatureFlagCommand>
{
    public IEnumerable<ValidationError> Validate(UpdateFeatureFlagCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.Key))
        {
            yield return new ValidationError("key", "Flag açarı tələb olunur.");
        }

        if (request.RolloutPercent is < 0 or > 100)
        {
            yield return new ValidationError("rolloutPercent", "Rollout faizi 0 ilə 100 arasında olmalıdır.");
        }

        foreach (var kioskId in request.KioskOverrides?.Keys ?? [])
        {
            if (!Guid.TryParse(kioskId, out _))
            {
                yield return new ValidationError("kioskOverrides", $"'{kioskId}' düzgün kiosk identifikatoru deyil.");
            }
        }
    }
}

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

public sealed class GetMobileFeaturesHandler
    : IRequestHandler<GetMobileFeaturesQuery, IReadOnlyDictionary<string, bool>>
{
    private readonly IFeatureFlagEvaluator _evaluator;

    public GetMobileFeaturesHandler(IFeatureFlagEvaluator evaluator) => _evaluator = evaluator;

    public Task<IReadOnlyDictionary<string, bool>> Handle(
        GetMobileFeaturesQuery request, CancellationToken cancellationToken)
        => _evaluator.EvaluateAllAsync(FeaturePlatform.Mobile, kioskId: null, request.UserId, cancellationToken);
}

public sealed class ListFeatureFlagsHandler
    : IRequestHandler<ListFeatureFlagsQuery, IReadOnlyList<FeatureFlagDetails>>
{
    private readonly IFeatureFlagRepository _flags;

    public ListFeatureFlagsHandler(IFeatureFlagRepository flags) => _flags = flags;

    public async Task<IReadOnlyList<FeatureFlagDetails>> Handle(
        ListFeatureFlagsQuery request, CancellationToken cancellationToken)
    {
        var flags = await _flags.ListAsync(cancellationToken).ConfigureAwait(false);

        return [.. flags.Select(ToDetails)];
    }

    internal static FeatureFlagDetails ToDetails(FeatureFlag flag) => new(
        flag.Id,
        flag.Key,
        flag.Description,
        flag.DefaultEnabled,
        flag.Platforms,
        flag.KioskOverrides,
        flag.UserAllowlist,
        flag.RolloutPercent,
        flag.UpdatedAt);
}

public sealed class UpdateFeatureFlagHandler : IRequestHandler<UpdateFeatureFlagCommand, FeatureFlagDetails>
{
    private readonly IFeatureFlagRepository _flags;
    private readonly IAuditWriter _audit;
    private readonly IAdminNotifier _notifier;
    private readonly IClock _clock;

    public UpdateFeatureFlagHandler(
        IFeatureFlagRepository flags, IAuditWriter audit, IAdminNotifier notifier, IClock clock)
    {
        _flags = flags;
        _audit = audit;
        _notifier = notifier;
        _clock = clock;
    }

    public async Task<FeatureFlagDetails> Handle(
        UpdateFeatureFlagCommand request, CancellationToken cancellationToken)
    {
        var flag = await _flags.FindByKeyAsync(request.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException("FeatureFlag", request.Key);

        var before = System.Text.Json.JsonSerializer.Serialize(new
        {
            flag.DefaultEnabled,
            flag.RolloutPercent,
            flag.KioskOverrides,
            flag.UserAllowlist,
        });

        flag.DefaultEnabled = request.DefaultEnabled;
        flag.RolloutPercent = request.RolloutPercent;
        flag.KioskOverrides = request.KioskOverrides is null
            ? []
            : new Dictionary<string, bool>(request.KioskOverrides, StringComparer.Ordinal);
        flag.UserAllowlist = [.. request.UserAllowlist ?? []];
        flag.UpdatedBy = request.UpdatedBy;
        flag.UpdatedAt = _clock.UtcNow;

        await _flags.UpdateAsync(flag, cancellationToken).ConfigureAwait(false);

        await _audit.WriteAsync(
                new AuditEntry(
                    request.UpdatedBy,
                    "features.update",
                    "FeatureFlag",
                    flag.Key,
                    before,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        flag.DefaultEnabled,
                        flag.RolloutPercent,
                        flag.KioskOverrides,
                        flag.UserAllowlist,
                    }),
                    request.Ip,
                    null),
                cancellationToken)
            .ConfigureAwait(false);

        // Kiosks and panels pick the change up over SignalR rather than at
        // their next poll (plan section 2.16).
        await _notifier.FeatureFlagsChangedAsync(cancellationToken).ConfigureAwait(false);

        return ListFeatureFlagsHandler.ToDetails(flag);
    }
}

/// <summary>
/// Guards an endpoint behind a feature flag (plan section 2.16).
/// </summary>
/// <remarks>
/// Hiding a feature in the UI is not enough: a flag that is off has to close
/// the endpoint too, or the capability is still reachable.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireFeatureAttribute : Attribute
{
    public RequireFeatureAttribute(string key) => Key = key;

    public string Key { get; }
}
