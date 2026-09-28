using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Analytics;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Analytics;

/// <summary>The analytics events (plan section 2.17, <c>analytics_events</c>).</summary>
public interface IAnalyticsEventRepository
{
    Task AddManyAsync(IReadOnlyCollection<AnalyticsEvent> events, CancellationToken cancellationToken = default);
}

/// <summary>One event as the kiosk sends it.</summary>
public sealed record AnalyticsEventInput(string? Event, string? SessionId, DateTime At, IReadOnlyDictionary<string, string?>? Props);

/// <summary>
/// A batch of a kiosk's analytics events (plan section 2.17). The batch
/// carries an Idempotency-Key, so a batch sent again after a lost answer is
/// stored once.
/// </summary>
public sealed record RecordKioskAnalyticsCommand(Guid DeviceId, IReadOnlyList<AnalyticsEventInput> Events)
    : IRequest<AnalyticsBatchResult>;

/// <param name="Rejected">Events that were not stored: unknown, badly dated or without a session.</param>
public sealed record AnalyticsBatchResult(int Accepted, int Rejected);

/// <inheritdoc cref="RecordKioskAnalyticsCommand" />
public sealed class RecordKioskAnalyticsValidator : IValidator<RecordKioskAnalyticsCommand>
{
    /// <summary>The most a kiosk sends at once.</summary>
    public const int MaxBatch = 200;

    public IEnumerable<ValidationError> Validate(RecordKioskAnalyticsCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Events is null || request.Events.Count > MaxBatch)
        {
            yield return new ValidationError("events", "analytics.batchInvalid");
        }
    }
}

/// <inheritdoc cref="RecordKioskAnalyticsCommand" />
/// <remarks>
/// A bad event is dropped, not the batch: the kiosk would otherwise send the
/// same batch for ever. Not transactional - a time-series collection takes no
/// writes inside a transaction, and an analytics event is not a business
/// state change.
/// </remarks>
public sealed class RecordKioskAnalyticsHandler : IRequestHandler<RecordKioskAnalyticsCommand, AnalyticsBatchResult>
{
    /// <summary>How old an event may be: a kiosk that was offline for a week still reports its week.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>A kiosk clock a little ahead is tolerated.</summary>
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    private readonly IKioskDeviceRepository _devices;
    private readonly IAnalyticsEventRepository _events;
    private readonly IClock _clock;

    public RecordKioskAnalyticsHandler(IKioskDeviceRepository devices, IAnalyticsEventRepository events, IClock clock)
    {
        _devices = devices;
        _events = events;
        _clock = clock;
    }

    public async Task<AnalyticsBatchResult> Handle(RecordKioskAnalyticsCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var device = await _devices.FindByIdAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);

        if (device?.KioskId is not { } kioskId)
        {
            throw new DomainException("devices.notBound");
        }

        var now = _clock.UtcNow;
        var kept = new List<AnalyticsEvent>();

        foreach (var input in request.Events)
        {
            var at = input.At.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(input.At, DateTimeKind.Utc) : input.At.ToUniversalTime();

            if (AnalyticsCatalog.KioskProps(input.Event, input.Props) is not { } props
                || string.IsNullOrWhiteSpace(input.SessionId)
                || input.SessionId.Length > 64
                || at < now - MaxAge
                || at > now + MaxSkew)
            {
                continue;
            }

            kept.Add(new AnalyticsEvent
            {
                Timestamp = at,
                Source = AnalyticsSource.Kiosk,
                KioskId = kioskId,
                SessionId = input.SessionId,
                Event = input.Event!,
                Props = props,
            });
        }

        if (kept.Count > 0)
        {
            await _events.AddManyAsync(kept, cancellationToken).ConfigureAwait(false);
        }

        return new AnalyticsBatchResult(kept.Count, request.Events.Count - kept.Count);
    }
}
