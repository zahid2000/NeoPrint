using Microsoft.Extensions.Logging;

using NeoPrint.Kiosk.Core.Updates;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

/// <summary>
/// The night's sleep, as the configuration carries it (<c>sleep</c>, T-448):
/// the settings <c>Kiosk.SleepEnabled</c>, <c>Kiosk.SleepWindow</c> and
/// <c>Kiosk.SleepAfterIdleMinutes</c>. A backend that sends none: no sleep.
/// </summary>
/// <param name="Window">The night, the kiosk's local time ("23:00-06:00"); it wakes at its end.</param>
/// <param name="AfterIdleMinutes">How long the kiosk has had no customer and nothing at the printer before it sleeps.</param>
public sealed record KioskSleepSettings(bool Enabled = false, string Window = "23:00-06:00", int AfterIdleMinutes = 30);

/// <summary>
/// Puts the kiosk to sleep at night and lets it wake itself (T-448). It never
/// sleeps while it works: only inside the night window, only after
/// <c>AfterIdleMinutes</c> with no customer, no phone, nothing printing and
/// nothing in the queue (<see cref="IUpdateHost.IsIdle"/>), and never while an
/// update is under way or waits for its moment - the update window lies in
/// the night too. The heartbeat before it tells the backend when the kiosk
/// wakes, so a sleeping kiosk is not reported offline.
/// </summary>
public sealed class NightSleepCoordinator
{
    /// <summary>How often the kiosk looks.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    /// <summary>A sleep shorter than this is not worth it: the night is nearly over.</summary>
    public static readonly TimeSpan MinSleep = TimeSpan.FromMinutes(15);

    /// <summary>The quiet waited for, whatever the setting says (the backend checks the same range).</summary>
    public const int MinIdleMinutes = 5;

    public const int MaxIdleMinutes = 240;

    private readonly ISystemSleep _sleep;
    private readonly IUpdateHost _host;
    private readonly Func<KioskSleepSettings?> _settings;
    private readonly Func<bool> _updateBusy;
    private readonly Func<DateTime, CancellationToken, Task> _announce;
    private readonly Action _woke;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TimeZoneInfo _zone;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private DateTime? _quietSince;
    private DateTime? _awakeUntil;

    /// <param name="announce">The heartbeat that tells the backend when the kiosk wakes.</param>
    /// <param name="woke">Awake again: the heartbeats are ordinary ones from now on.</param>
    public NightSleepCoordinator(
        ISystemSleep sleep,
        IUpdateHost host,
        Func<KioskSleepSettings?> settings,
        Func<bool> updateBusy,
        Func<DateTime, CancellationToken, Task> announce,
        Action woke,
        IClock clock,
        ILogger logger,
        TimeZoneInfo? zone = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sleep = sleep;
        _host = host;
        _settings = settings;
        _updateBusy = updateBusy;
        _announce = announce;
        _woke = woke;
        _clock = clock;
        _logger = logger;
        _zone = zone ?? TimeZoneInfo.Local;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The window, or null while the night's sleep is off or its setting unreadable.</summary>
    public static UpdateWindow? WindowOf(KioskSleepSettings? settings)
        => settings is { Enabled: true } ? UpdateWindow.TryParse(settings.Window) : null;

    /// <summary>The window's next end after <paramref name="local"/>: when the kiosk wakes, its local time.</summary>
    public static DateTime WakeAt(UpdateWindow window, DateTime local)
    {
        ArgumentNullException.ThrowIfNull(window);

        var wake = local.Date + window.To.ToTimeSpan();
        return wake <= local ? wake.AddDays(1) : wake;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await StepAsync(cancellationToken).ConfigureAwait(false);
            await _delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One look: sleeps when it is time, and returns once awake.</summary>
    /// <returns>Whether the kiosk slept.</returns>
    public async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, _zone);
        var settings = _settings();
        var window = WindowOf(settings);

        if (window is null || !window.Contains(TimeOnly.FromDateTime(local)))
        {
            _quietSince = null;
            _awakeUntil = null;
            return false;
        }

        // It could not sleep earlier tonight: it stays awake until the night is over.
        if (_awakeUntil is { } awake && now < awake)
        {
            return false;
        }

        if (!_host.IsIdle || _updateBusy())
        {
            _quietSince = null;
            return false;
        }

        _quietSince ??= now;
        var idle = TimeSpan.FromMinutes(Math.Clamp(settings!.AfterIdleMinutes, MinIdleMinutes, MaxIdleMinutes));

        if (now - _quietSince.Value < idle)
        {
            return false;
        }

        var wakeAt = ToUtc(WakeAt(window, local));

        if (wakeAt - now < MinSleep)
        {
            return false;
        }

        _logger.LogInformation(
            "No customer for {Minutes} minutes at night: the kiosk sleeps until {WakeAt:O}.", (int)idle.TotalMinutes, wakeAt);

        try
        {
            await _announce(wakeAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The backend will see a silent kiosk; the sleep is still worth it.
            _logger.LogWarning(exception, "The backend could not be told about the night's sleep.");
        }

        bool slept;
        try
        {
            slept = await _sleep.SleepUntilAsync(wakeAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The kiosk could not be put to sleep.");
            slept = false;
        }

        _quietSince = null;
        _woke();

        if (!slept)
        {
            _awakeUntil = wakeAt;
            _logger.LogWarning("The kiosk could not sleep; it stays awake until {WakeAt:O}.", wakeAt);
            return false;
        }

        _logger.LogInformation("The kiosk is awake again.");
        return true;
    }

    private DateTime ToUtc(DateTime local)
    {
        // A wake time that falls into a clock change is moved past it.
        while (_zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), _zone);
    }
}
