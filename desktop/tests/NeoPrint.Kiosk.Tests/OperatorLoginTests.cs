using FluentAssertions;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The admin corner with an operator's QR (T-392): the kiosk shows a QR, the
/// menu opens in the name of the operator who confirmed it, and the changes
/// made there go to the backend under that login.
/// </summary>
public sealed class OperatorLoginTests
{
    private sealed class FakeLogins : IOperatorLogins
    {
        public int Started { get; private set; }

        public bool Offline { get; set; }

        public OperatorLoginState? Answer { get; set; }

        public List<(string Action, string? Printer, int? Limit)> Actions { get; } = [];

        public List<Guid> Ended { get; } = [];

        public readonly Guid Id = Guid.NewGuid();

        public Task<OperatorLoginStarted> StartAsync(CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            Started++;
            return Task.FromResult(new OperatorLoginStarted(Id, $"neoprint://kiosk-login/{Started:x32}", DateTime.UtcNow.AddMinutes(2)));
        }

        public Task<OperatorLoginState> GetAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(Answer ?? new OperatorLoginState(id, "Pending", null, DateTime.UtcNow.AddMinutes(2), false, false, false));

        public Task ActAsync(Guid id, string action, string? printer, int? limit, CancellationToken cancellationToken)
        {
            Actions.Add((action, printer, limit));
            return Task.CompletedTask;
        }

        public Task EndAsync(Guid id, CancellationToken cancellationToken)
        {
            Ended.Add(id);
            return Task.CompletedTask;
        }
    }

    private static KioskConfiguration Configuration() => new(
        Guid.NewGuid(), "KSK-0042", "Metro", "Online", "az", "HP LaserJet", new PrinterCapabilityFlags(true, true, true),
        [new ConfiguredPrinter("HP LaserJet", "HP", true, new PrinterCapabilityFlags(true, true, false)), new ConfiguredPrinter("Canon", "Canon", true, new PrinterCapabilityFlags(true, true, true))],
        "pin-hash", 5, 50, 200, null, new Dictionary<string, PaperStockLevel>(), new Dictionary<string, bool>(), 15, 24, DateTime.UtcNow);

    private static (AdminMenuViewModel Menu, FakeLogins Logins, List<int> Polls) Menu()
    {
        var logins = new FakeLogins();
        var polls = new List<int>();
        var menu = new AdminMenuViewModel(
            () => "pin-hash",
            logins: logins,
            configuration: Configuration,
            delay: (_, _) =>
            {
                polls.Add(polls.Count);
                // The operator confirms on the second look.
                if (polls.Count == 2)
                {
                    logins.Answer = new OperatorLoginState(logins.Id, "Approved", "Elvin M.", DateTime.UtcNow.AddMinutes(15), true, true, false);
                }

                return Task.CompletedTask;
            })
        { Language = "az" };
        return (menu, logins, polls);
    }

    [Fact]
    public async Task The_kiosk_shows_a_qr_and_the_menu_opens_in_the_name_of_the_operator_who_confirmed_it()
    {
        var (menu, logins, polls) = Menu();

        // Open shows the QR and looks until the operator confirmed it.
        menu.Open(safeMode: false);
        await menu.WaitForOperatorAsync();

        logins.Started.Should().Be(1);
        polls.Should().HaveCount(2, "the kiosk looked until the operator confirmed");
        menu.Stage.Should().Be(AdminStage.Menu);
        menu.OperatorText.Should().Be("Menyu Elvin M. adından açıqdır");
        menu.CanSelectPrinter.Should().BeTrue();
        menu.CanPause.Should().BeFalse("the account may not stop orders here");
        menu.Printers.Should().Equal(new AdminPrinterChoice("HP LaserJet", true), new AdminPrinterChoice("Canon", false));
        menu.QueueLimit.Should().Be(5);

        await menu.SelectPrinterCommand.ExecuteAsync("Canon");
        await menu.SetQueueLimitCommand.ExecuteAsync("3");
        logins.Actions.Should().Equal(("SelectPrinter", "Canon", null), ("SetQueueLimit", null, 3));
        menu.Printers.Single(printer => printer.IsSelected).Name.Should().Be("Canon");
        menu.QueueIsThree.Should().BeTrue();
        menu.Notice.Should().NotBeNull();

        // Closed: nothing more in the operator's name.
        menu.CloseCommand.Execute(null);
        logins.Ended.Should().Equal(logins.Id);
        menu.HasOperator.Should().BeFalse();
    }

    [Fact]
    public async Task Without_the_server_the_menu_says_so_and_the_pin_is_the_way_in()
    {
        var (menu, logins, _) = Menu();
        logins.Offline = true;
        menu.Open(safeMode: false);
        await menu.WaitForOperatorAsync();

        menu.Stage.Should().Be(AdminStage.Qr);
        menu.LoginQr.Should().BeNull();
        menu.Error.Should().NotBeNull();

        menu.UsePinCommand.Execute(null);
        menu.Stage.Should().Be(AdminStage.Pin);
        menu.HasOperator.Should().BeFalse("a PIN opens the machine's own actions only");
    }

    [Fact]
    public void A_kiosk_not_activated_yet_has_no_qr_only_the_service_pin()
    {
        var menu = new AdminMenuViewModel(() => null, () => "service-hash", logins: new FakeLogins()) { Language = "az" };
        menu.Open(safeMode: false);
        menu.Stage.Should().Be(AdminStage.Pin);
    }
}
