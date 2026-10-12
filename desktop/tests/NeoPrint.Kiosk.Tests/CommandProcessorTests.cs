using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Printing;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// Remote commands on the kiosk (plan section 7.8): pause and resume follow
/// the configuration, a restart waits for the printer and is answered by the
/// application that comes back, a reboot is judged by the machine's uptime.
/// </summary>
public sealed class CommandProcessorTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeAppPaths _paths = new();
    private readonly Backend _backend = new();
    private readonly FakeSystemControl _system = new();
    private readonly TestClock _clock = new(Start);
    private PrintQueueWorker? _worker;
    private int _reloads;
    private int _leftOnPurpose;
    private bool _safeMode;

    public CommandProcessorTests() => _paths.EnsureCreated();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; set; } = now;
    }

    /// <summary>A backend that takes acknowledgements and answers, and remembers them.</summary>
    private sealed class Backend : HttpMessageHandler
    {
        public List<string> Acknowledged { get; } = [];

        /// <summary>The backend is not taking acknowledgements: the kiosk cannot mark a command.</summary>
        public bool RefuseAcknowledgements { get; set; }

        public List<(string Id, bool Succeeded, string? MessageKey)> Answers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/register", StringComparison.Ordinal))
            {
                return Json(new { deviceId = Guid.NewGuid(), activated = true, kioskId = Guid.NewGuid(), kioskCode = "KSK-0001" });
            }

            if (path.EndsWith("/token", StringComparison.Ordinal) || path.EndsWith("/challenge", StringComparison.Ordinal))
            {
                return Json(new { accessToken = "kiosk-token", expiresAt = DateTime.UtcNow.AddHours(1), tokenType = "kiosk", nonce = "nonce" });
            }

            var parts = path.Split('/');

            if (path.EndsWith("/ack", StringComparison.Ordinal))
            {
                if (RefuseAcknowledgements)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                Acknowledged.Add(parts[^2]);
                return Json(new { });
            }

            if (path.EndsWith("/result", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Answers.Add((
                    parts[^2],
                    body.RootElement.GetProperty("succeeded").GetBoolean(),
                    body.RootElement.GetProperty("messageKey").GetString()));

                return Json(new { });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class NoTestPage : ITestPageSource
    {
        public Task<string> CreateAsync(string folder, TestPageInfo info, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("These tests print nothing.");
    }

    /// <summary>A machine that refuses to restart: sudo is not allowed, say.</summary>
    private sealed class RefusingSystem : ISystemControl
    {
        public Task RestartApplicationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RebootAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Could not start sudo systemctl reboot.");

        public Task OpenMaintenanceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A machine that goes down: the call never comes back, as when the process dies with it.</summary>
    private sealed class GoingDown : ISystemControl
    {
        public int Reboots { get; private set; }

        public int ApplicationRestarts { get; private set; }

        public Task RestartApplicationAsync(CancellationToken cancellationToken = default)
        {
            ApplicationRestarts++;
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task RebootAsync(CancellationToken cancellationToken = default)
        {
            Reboots++;
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task OpenMaintenanceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private async Task<CommandProcessor> ProcessorAsync(long? uptimeSeconds = null, ISystemControl? system = null)
    {
        var http = new HttpClient(_backend) { BaseAddress = new Uri("http://backend.test/") };
        var api = new KioskApiClient(http, new FakeDeviceKeyStore(_paths), NullLogger<KioskApiClient>.Instance);

        await api.RegisterAsync(
            new MachineFingerprint("test-host", "Linux", "6.1", "machine-1", [], "CPU", 8, "1920x1080"), "Linux", "0.7.0", enrollmentToken: null, printers: []);

        var printer = new FakePrinterService();
        var spool = new SpoolStore(_paths, new FakeSpoolKeyStore(_paths));

        _worker = new PrintQueueWorker(
            new ApiPrintBackend(api), printer, spool, new JobJournal(_paths), () => FakePrinterService.FakePrinterName,
            NullLogger<PrintQueueWorker>.Instance);

        return new CommandProcessor(
            api,
            new PrinterReporter(printer, (_, _) => Task.CompletedTask, NullLogger<PrinterReporter>.Instance),
            _worker,
            printer,
            new NoTestPage(),
            spool,
            () => null,
            (_, _, _) => [],
            NullLogger<CommandProcessor>.Instance,
            reloadConfiguration: _ =>
            {
                _reloads++;
                return Task.CompletedTask;
            },
            system: system ?? _system,
            leavingOnPurpose: () => _leftOnPurpose++,
            uptimeSeconds: _ => Task.FromResult(uptimeSeconds),
            clock: _clock,
            safeMode: () => _safeMode,
            pending: new PendingCommandStore(_paths.StateDirectory))
        {
            RebootGrace = TimeSpan.FromMilliseconds(40),
        };
    }

    [Theory]
    [InlineData("Pause", "kioskCommands.paused")]
    [InlineData("Resume", "kioskCommands.resumed")]
    public async Task Pause_and_resume_read_the_configuration_again_and_say_so(string type, string expected)
    {
        var processor = await ProcessorAsync();
        var id = Guid.NewGuid();

        await processor.HandleAsync(id, type, CancellationToken.None);

        _reloads.Should().Be(1, "the pause is part of the configuration: the screen follows it");
        _backend.Answers.Should().Equal((id.ToString(), true, expected));
        _system.ApplicationRestarts.Should().Be(0);
    }

    [Fact]
    public async Task A_restart_is_acknowledged_carried_out_and_not_answered_by_the_application_that_is_leaving()
    {
        var processor = await ProcessorAsync();
        var id = Guid.NewGuid();

        await processor.HandleAsync(id, "RestartApp", CancellationToken.None);

        _backend.Acknowledged.Should().Equal(id.ToString());
        _system.ApplicationRestarts.Should().Be(1);
        _leftOnPurpose.Should().Be(1, "a restart somebody asked for is not a crash");
        _backend.Answers.Should().BeEmpty("the application that comes back answers");
    }

    [Fact]
    public async Task The_application_that_came_back_answers_the_restart_and_does_not_restart_again()
    {
        // The command was sent, the application restarted, this is the new one.
        var sentAt = Start.AddMinutes(-2);
        var processor = await ProcessorAsync();
        var id = Guid.NewGuid();

        await processor.HandleAsync(id, "RestartApp", CancellationToken.None, sentAt);

        _backend.Answers.Should().Equal((id.ToString(), true, "kioskCommands.restarted"));
        _backend.Acknowledged.Should().BeEmpty();
        _system.ApplicationRestarts.Should().Be(0, "starting was the restart - also for a kiosk that was switched off when it was sent");
    }

    [Fact]
    public async Task A_restart_sent_while_the_kiosk_had_no_connection_is_carried_out_when_it_is_read_back()
    {
        var processor = await ProcessorAsync();
        _clock.UtcNow = Start.AddMinutes(30);

        // Sent ten minutes ago, twenty minutes after this process started.
        await processor.HandleAsync(Guid.NewGuid(), "RestartApp", CancellationToken.None, Start.AddMinutes(20));

        _system.ApplicationRestarts.Should().Be(1);
    }

    [Fact]
    public async Task A_restart_waits_for_the_job_at_the_printer()
    {
        var processor = await ProcessorAsync();
        var printing = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobAtThePrinter = _worker!.WithPrinterAsync(() => printing.Task, CancellationToken.None);

        var id = Guid.NewGuid();
        var restart = processor.HandleAsync(id, "RestartApp", CancellationToken.None);

        for (var i = 0; i < 100 && _backend.Acknowledged.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        _backend.Acknowledged.Should().Equal([id.ToString()], "the panel hears at once that the kiosk has the command");
        _system.ApplicationRestarts.Should().Be(0, "a page is still coming out");

        printing.SetResult(0);
        await jobAtThePrinter;
        await restart;

        _system.ApplicationRestarts.Should().Be(1);
    }

    [Fact]
    public async Task A_machine_that_restarted_since_the_command_answers_rebooted()
    {
        // Sent five minutes ago; the machine has been up for one.
        var processor = await ProcessorAsync(uptimeSeconds: 60);
        var id = Guid.NewGuid();

        await processor.HandleAsync(id, "Reboot", CancellationToken.None, Start.AddMinutes(-5));

        _backend.Answers.Should().Equal((id.ToString(), true, "kioskCommands.rebooted"));
        _system.Reboots.Should().Be(0, "it has been done");
    }

    [Fact]
    public async Task An_application_that_came_back_on_a_machine_that_never_went_down_says_so()
    {
        // Acknowledged five minutes ago; the machine has been up for an hour
        // and the note on disk is gone.
        var processor = await ProcessorAsync(uptimeSeconds: 3600);
        var id = Guid.NewGuid();

        await processor.HandleAsync(id, "Reboot", CancellationToken.None, Start.AddMinutes(-5), acknowledged: true);

        _backend.Answers.Should().Equal((id.ToString(), false, "kioskCommands.rebootNotDone"));
        _system.Reboots.Should().Be(0, "not tried a second time by itself: somebody has to look");
    }

    // -----------------------------------------------------------------------
    // Marked before it is carried out: no restart and no reboot ever repeats
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_reboot_is_marked_before_the_machine_goes_down_and_is_not_carried_out_again_by_what_comes_back()
    {
        // First boot: the command arrives, the machine goes down.
        var machine = new GoingDown();
        using var goingDown = new CancellationTokenSource();
        var first = await ProcessorAsync(uptimeSeconds: 7200, system: machine);
        var id = Guid.NewGuid();

        var neverReturns = first.HandleAsync(id, "Reboot", goingDown.Token);

        for (var i = 0; i < 200 && machine.Reboots == 0; i++)
        {
            await Task.Delay(10);
        }

        machine.Reboots.Should().Be(1);
        _backend.Acknowledged.Should().Equal([id.ToString()], "marked on the backend before the machine was asked to restart");
        new PendingCommandStore(_paths.StateDirectory).Find(id).Should().NotBeNull("and noted on disk");
        _backend.Answers.Should().BeEmpty();

        // Second boot. Its clock is an hour behind the server's - to this
        // kiosk the command looks as if it were sent in the future, so
        // neither "older than the process" nor "older than the boot" holds.
        // What it has is the mark.
        _clock.UtcNow = Start.AddHours(-1);
        var afterReboot = await ProcessorAsync(uptimeSeconds: 40);

        await afterReboot.HandleAsync(id, "Reboot", CancellationToken.None, sentAt: Start, acknowledged: true);

        _system.Reboots.Should().Be(0, "the machine must not restart for the same command twice");
        _backend.Answers.Should().Equal((id.ToString(), true, "kioskCommands.rebooted"));
        new PendingCommandStore(_paths.StateDirectory).Find(id).Should().BeNull("answered: the note is done with");

        // A third start that still finds the command open - the answer was
        // lost on its way - answers again and still does not restart anything.
        var third = await ProcessorAsync(uptimeSeconds: 400);
        await third.HandleAsync(id, "Reboot", CancellationToken.None, sentAt: Start, acknowledged: true);

        _system.Reboots.Should().Be(0);
        machine.Reboots.Should().Be(1, "one command, one reboot");

        await goingDown.CancelAsync();
        await ((Func<Task>)(() => neverReturns)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_note_on_disk_alone_is_enough_when_the_backend_cannot_be_asked()
    {
        // Noted, the machine went down; what comes back reads the command as
        // merely "sent" - say the acknowledgement's answer never arrived.
        var machine = new GoingDown();
        using var goingDown = new CancellationTokenSource();
        var first = await ProcessorAsync(uptimeSeconds: 7200, system: machine);
        var id = Guid.NewGuid();
        _ = first.HandleAsync(id, "Reboot", goingDown.Token);

        for (var i = 0; i < 200 && machine.Reboots == 0; i++)
        {
            await Task.Delay(10);
        }

        _clock.UtcNow = Start.AddHours(-1);
        var afterReboot = await ProcessorAsync(uptimeSeconds: 40);
        await afterReboot.HandleAsync(id, "Reboot", CancellationToken.None, sentAt: Start, acknowledged: false);

        _system.Reboots.Should().Be(0);
        _backend.Answers.Should().Equal((id.ToString(), true, "kioskCommands.rebooted"));

        await goingDown.CancelAsync();
    }

    [Fact]
    public async Task A_restart_is_marked_the_same_way_and_does_not_repeat_on_a_kiosk_whose_clock_is_wrong()
    {
        var machine = new GoingDown();
        using var goingDown = new CancellationTokenSource();
        var first = await ProcessorAsync(system: machine);
        var id = Guid.NewGuid();
        _ = first.HandleAsync(id, "RestartApp", goingDown.Token);

        for (var i = 0; i < 200 && machine.ApplicationRestarts == 0; i++)
        {
            await Task.Delay(10);
        }

        _backend.Acknowledged.Should().Equal(id.ToString());

        // The application that came back, with a clock an hour behind.
        _clock.UtcNow = Start.AddHours(-1);
        var cameBack = await ProcessorAsync();
        await cameBack.HandleAsync(id, "RestartApp", CancellationToken.None, sentAt: Start, acknowledged: true);

        _system.ApplicationRestarts.Should().Be(0);
        _backend.Answers.Should().Equal((id.ToString(), true, "kioskCommands.restarted"));

        await goingDown.CancelAsync();
    }

    [Fact]
    public async Task A_command_that_could_not_be_marked_is_not_carried_out()
    {
        var processor = await ProcessorAsync(uptimeSeconds: 7200);
        var id = Guid.NewGuid();
        _backend.RefuseAcknowledgements = true;

        await processor.HandleAsync(id, "Reboot", CancellationToken.None);
        await processor.HandleAsync(Guid.NewGuid(), "RestartApp", CancellationToken.None);

        _system.Reboots.Should().Be(0, "an unmarked reboot could repeat itself");
        _system.ApplicationRestarts.Should().Be(0);
        new PendingCommandStore(_paths.StateDirectory).Find(id).Should().BeNull();
        _backend.Answers.Should().BeEmpty("it is still open and is taken again later");

        // The backend is back: the same command is taken and carried out.
        _backend.RefuseAcknowledgements = false;
        await processor.HandleAsync(id, "Reboot", CancellationToken.None, sentAt: Start.AddSeconds(1));

        _system.Reboots.Should().Be(1);
    }

    [Fact]
    public void The_note_survives_the_process_and_a_damaged_one_is_no_note()
    {
        var id = Guid.NewGuid();
        new PendingCommandStore(_paths.StateDirectory).Note(new PendingCommand(id, "Reboot", Start, 7200));

        var read = new PendingCommandStore(_paths.StateDirectory);
        read.Find(id).Should().Be(new PendingCommand(id, "Reboot", Start, 7200));
        read.Find(Guid.NewGuid()).Should().BeNull("a note about another command says nothing about this one");

        read.Clear();
        new PendingCommandStore(_paths.StateDirectory).Find(id).Should().BeNull();

        File.WriteAllText(Path.Combine(_paths.StateDirectory, "pending-command.json"), "{ not json");
        new PendingCommandStore(_paths.StateDirectory).Find(id).Should().BeNull();
    }

    [Fact]
    public async Task A_reboot_is_acknowledged_and_asked_of_the_machine()
    {
        var processor = await ProcessorAsync(uptimeSeconds: 3600);
        var id = Guid.NewGuid();

        await processor.HandleAsync(id, "Reboot", CancellationToken.None);

        _backend.Acknowledged.Should().Equal(id.ToString());
        _system.Reboots.Should().Be(1);

        // The fake machine stays up; a real one takes this process with it.
        // Still here after the grace period is a reboot that did not happen.
        _backend.Answers.Should().Equal((id.ToString(), false, "kioskCommands.rebootRefused"));
    }

    [Fact]
    public async Task A_machine_that_refuses_to_restart_is_reported_and_the_kiosk_keeps_working()
    {
        var processor = await ProcessorAsync(system: new RefusingSystem());
        var id = Guid.NewGuid();

        await processor.HandleAsync(id, "Reboot", CancellationToken.None);

        _backend.Answers.Should().Equal((id.ToString(), false, "kioskCommands.rebootRefused"));

        // The next command is taken as usual.
        await processor.HandleAsync(Guid.NewGuid(), "Pause", CancellationToken.None);
        _reloads.Should().Be(1);
    }

    [Fact]
    public async Task A_kiosk_in_safe_mode_can_still_be_restarted_and_leaves_printer_commands_for_later()
    {
        var processor = await ProcessorAsync();
        _safeMode = true;

        // A kiosk that keeps crashing is the one that most needs this.
        var restart = Guid.NewGuid();
        await processor.HandleAsync(restart, "RestartApp", CancellationToken.None);
        _system.ApplicationRestarts.Should().Be(1);

        await processor.HandleAsync(Guid.NewGuid(), "Pause", CancellationToken.None);
        _reloads.Should().Be(1);

        // It prints nothing in safe mode: the test page is neither printed nor refused.
        var testPage = Guid.NewGuid();
        await processor.HandleAsync(testPage, "TestPrint", CancellationToken.None);
        _backend.Answers.Should().NotContain(answer => answer.Id == testPage.ToString());

        // Safe mode cleared: the command, still open, is taken now.
        _safeMode = false;
        await processor.HandleAsync(testPage, "TestPrint", CancellationToken.None);
        _backend.Answers.Should().Contain(answer => answer.Id == testPage.ToString());
    }

    [Fact]
    public async Task A_command_delivered_twice_is_carried_out_once_and_an_unknown_one_is_refused_in_words()
    {
        var processor = await ProcessorAsync();
        var id = Guid.NewGuid();

        // Over the hub, and again when the open commands are read back.
        await processor.HandleAsync(id, "Pause", CancellationToken.None);
        await processor.HandleAsync(id, "Pause", CancellationToken.None, Start.AddSeconds(-1));

        _reloads.Should().Be(1);
        _backend.Answers.Should().ContainSingle();

        var unknown = Guid.NewGuid();
        await processor.HandleAsync(unknown, "UpdateNow", CancellationToken.None);

        _backend.Answers[^1].Should().Be((unknown.ToString(), false, "kioskCommands.unsupported"));
    }
}
