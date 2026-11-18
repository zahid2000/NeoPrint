using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Updates;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk's night sleep (T-448): never while it works - only in the night
/// window, after the quiet the panel set, with no update under way - and it
/// wakes itself at the window's end, having told the backend when.
/// </summary>
public sealed class NightSleepTests
{
    // The tests run in UTC so that "23:00" is the kiosk's own clock wherever they run.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    private readonly TestClock _clock = new(new DateTime(2026, 11, 18, 23, 10, 0, DateTimeKind.Utc));
    private readonly Screen _screen = new();
    private readonly FakeSystemSleep _sleep = new() { Works = true };
    private readonly List<DateTime> _announced = [];
    private KioskSleepSettings? _settings = new(Enabled: true, Window: "23:00-06:00", AfterIdleMinutes: 30);
    private bool _updating;
    private int _woke;

    private NightSleepCoordinator Coordinator() => new(
        _sleep,
        _screen,
        () => _settings,
        () => _updating,
        (wakeAt, _) =>
        {
            _announced.Add(wakeAt);
            return Task.CompletedTask;
        },
        () => _woke++,
        _clock,
        NullLogger.Instance,
        Zone);

    [Fact]
    public async Task After_the_quiet_at_night_it_sleeps_until_six_and_says_so_first()
    {
        var night = Coordinator();

        (await night.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse("the quiet has only begun");

        _clock.Now = _clock.Now.AddMinutes(29);
        (await night.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        _sleep.Asked.Should().BeEmpty();

        _clock.Now = _clock.Now.AddMinutes(1);
        (await night.StepAsync(TestContext.Current.CancellationToken)).Should().BeTrue();

        var six = new DateTime(2026, 11, 19, 6, 0, 0, DateTimeKind.Utc);
        _announced.Should().Equal(six);
        _sleep.Asked.Should().Equal(six);
        _woke.Should().Be(1, "the heartbeats are ordinary ones again");
    }

    [Fact]
    public async Task A_customer_starts_the_quiet_over()
    {
        var night = Coordinator();
        await night.StepAsync(TestContext.Current.CancellationToken);

        _clock.Now = _clock.Now.AddMinutes(20);
        _screen.Blocker = "updates.waiting.customer";
        (await night.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        _clock.Now = _clock.Now.AddMinutes(5);
        _screen.Blocker = null;
        await night.StepAsync(TestContext.Current.CancellationToken);

        _clock.Now = _clock.Now.AddMinutes(29);
        (await night.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse("thirty quiet minutes have not passed since the customer");
        _sleep.Asked.Should().BeEmpty();

        _clock.Now = _clock.Now.AddMinutes(1);
        (await night.StepAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task It_never_sleeps_by_day_while_an_update_is_under_way_or_when_the_setting_is_off()
    {
        // By day: quiet for hours, awake.
        _clock.Now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var day = Coordinator();
        await day.StepAsync(TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddHours(5);
        (await day.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        // An update waits for its window, which lies in the night too.
        _clock.Now = new DateTime(2026, 11, 18, 23, 10, 0, DateTimeKind.Utc);
        _updating = true;
        var updating = Coordinator();
        await updating.StepAsync(TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddHours(1);
        (await updating.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        // Switched off in the panel, or a backend that sends nothing.
        _updating = false;
        _settings = _settings! with { Enabled = false };
        var off = Coordinator();
        await off.StepAsync(TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddHours(1);
        (await off.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        _settings = null;
        (await off.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();

        _sleep.Asked.Should().BeEmpty();
        _announced.Should().BeEmpty();
    }

    [Fact]
    public async Task Close_to_the_morning_it_stays_awake()
    {
        _clock.Now = new DateTime(2026, 11, 19, 5, 20, 0, DateTimeKind.Utc);
        var night = Coordinator();
        await night.StepAsync(TestContext.Current.CancellationToken);

        _clock.Now = _clock.Now.AddMinutes(30);
        (await night.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse("ten minutes of sleep are not worth it");
        _sleep.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_machine_that_cannot_sleep_is_not_asked_again_that_night()
    {
        _sleep.Works = false;
        var night = Coordinator();
        await night.StepAsync(TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddMinutes(30);

        (await night.StepAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
        _sleep.Asked.Should().HaveCount(1);
        _woke.Should().Be(1, "the beat after says it is up");

        _clock.Now = _clock.Now.AddHours(1);
        await night.StepAsync(TestContext.Current.CancellationToken);
        _sleep.Asked.Should().HaveCount(1);
    }

    [Fact]
    public void The_wake_time_is_the_windows_next_end_and_a_bad_window_means_no_sleep()
    {
        var window = UpdateWindow.TryParse("23:00-06:00")!;

        NightSleepCoordinator.WakeAt(window, new DateTime(2026, 11, 18, 23, 30, 0))
            .Should().Be(new DateTime(2026, 11, 19, 6, 0, 0));
        NightSleepCoordinator.WakeAt(window, new DateTime(2026, 11, 19, 2, 0, 0))
            .Should().Be(new DateTime(2026, 11, 19, 6, 0, 0));

        NightSleepCoordinator.WindowOf(new KioskSleepSettings(true, "23:00")).Should().BeNull();
        NightSleepCoordinator.WindowOf(new KioskSleepSettings(true, "06:00-06:00")).Should().BeNull();
        NightSleepCoordinator.WindowOf(new KioskSleepSettings(false, "23:00-06:00")).Should().BeNull();
    }

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime Now { get; set; } = now;

        public DateTime UtcNow => Now;
    }

    private sealed class Screen : IUpdateHost
    {
        public string? Blocker { get; set; }

        public bool IsIdle => Blocker is null;

        public void SetDraining(bool draining)
        {
        }

        public void SetUpdating(bool updating)
        {
        }
    }
}
