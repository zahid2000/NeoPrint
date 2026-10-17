using System.Text.RegularExpressions;

using MediatR;

using Microsoft.Extensions.Logging;

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

/// <summary>Why kiosk events were not kept, counted per kiosk and Baku day (T-282).</summary>
public interface IAnalyticsRejectionStore
{
    /// <summary>Adds to the day's counts (an increment, not a replace: every batch adds its own).</summary>
    Task AddAsync(Guid kioskId, string day, AnalyticsRejections counts, string? lastUnknown, DateTime at, CancellationToken cancellationToken = default);

    /// <summary>Every kiosk's days from <paramref name="fromDay"/> on.</summary>
    Task<IReadOnlyList<AnalyticsRejectionDay>> ListAsync(string fromDay, CancellationToken cancellationToken = default);
}

/// <param name="UnknownEvent">A name the catalogue does not list (nor an older name it maps).</param>
/// <param name="NoSession">No session id, or one too long.</param>
/// <param name="OutOfTime">Older than a week, or ahead of the server's clock.</param>
public sealed record AnalyticsRejections(int UnknownEvent, int NoSession, int OutOfTime)
{
    public int Total => UnknownEvent + NoSession + OutOfTime;
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

    private static readonly Regex UnsafeName = new("[^a-z0-9_.-]", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private readonly IKioskDeviceRepository _devices;
    private readonly IAnalyticsEventRepository _events;
    private readonly IAnalyticsRejectionStore _rejections;
    private readonly IClock _clock;
    private readonly ILogger<RecordKioskAnalyticsHandler> _logger;

    public RecordKioskAnalyticsHandler(
        IKioskDeviceRepository devices,
        IAnalyticsEventRepository events,
        IAnalyticsRejectionStore rejections,
        IClock clock,
        ILogger<RecordKioskAnalyticsHandler> logger)
    {
        _devices = devices;
        _events = events;
        _rejections = rejections;
        _clock = clock;
        _logger = logger;
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
        int unknown = 0, noSession = 0, outOfTime = 0;
        string? lastUnknown = null;

        foreach (var input in request.Events)
        {
            var at = input.At.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(input.At, DateTimeKind.Utc) : input.At.ToUniversalTime();

            // A kiosk from before T-214 still says payment_method / payment_ended (T-282).
            var (name, raw) = AnalyticsCatalog.Current(input.Event, input.Props);

            if (AnalyticsCatalog.KioskProps(name, raw) is not { } props)
            {
                unknown++;
                lastUnknown = Safe(input.Event);
                continue;
            }

            if (string.IsNullOrWhiteSpace(input.SessionId) || input.SessionId.Length > 64)
            {
                noSession++;
                continue;
            }

            if (at < now - MaxAge || at > now + MaxSkew)
            {
                outOfTime++;
                continue;
            }

            kept.Add(new AnalyticsEvent
            {
                Timestamp = at,
                Source = AnalyticsSource.Kiosk,
                KioskId = kioskId,
                SessionId = input.SessionId,
                Event = name!,
                Props = props,
            });
        }

        if (kept.Count > 0)
        {
            await _events.AddManyAsync(kept, cancellationToken).ConfigureAwait(false);
        }

        var rejected = new AnalyticsRejections(unknown, noSession, outOfTime);

        if (rejected.Total > 0)
        {
            // Not silent (T-282): the log says it, the meter counts it, and the
            // admin panel's "Arxa plan işləri" shows it per kiosk and day.
            _logger.LogWarning(
                "Kiosk {KioskId} sent {Rejected} analytics events that were not kept: {Unknown} unknown (last: {LastUnknown}), {NoSession} without a session, {OutOfTime} out of time.",
                kioskId,
                rejected.Total,
                unknown,
                lastUnknown,
                noSession,
                outOfTime);
            Count(unknown, "unknownEvent");
            Count(noSession, "noSession");
            Count(outOfTime, "outOfTime");

            await _rejections
                .AddAsync(kioskId, PilotDayBuilder.DayOf(now), rejected, lastUnknown, now, cancellationToken)
                .ConfigureAwait(false);
        }

        return new AnalyticsBatchResult(kept.Count, rejected.Total);
    }

    private static void Count(int count, string reason)
    {
        if (count > 0)
        {
            NeoPrintMetrics.AnalyticsEventsRejected.Add(count, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    /// <summary>A name a kiosk sent, safe to log and to show: its own characters only, cut short.</summary>
    private static string? Safe(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var safe = UnsafeName.Replace(name.Trim().ToLowerInvariant(), "?");

        return safe.Length > 40 ? safe[..40] : safe;
    }
}
