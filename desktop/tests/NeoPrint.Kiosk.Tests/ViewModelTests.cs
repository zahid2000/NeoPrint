using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Localization;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The shell and activation view models (plan sections 7.2, 7.3, 11).
/// </summary>
// With the handler tests: switching the screen language changes the
// process-wide KioskLanguage those tests read.
[Collection(KioskLanguageCollection.Name)]
public sealed class MainWindowViewModelTests
{
    [Fact]
    public void A_kiosk_that_is_not_activated_shows_the_activation_screen_and_no_kiosk_qr()
    {
        var model = new MainWindowViewModel();

        model.Apply(new EnrollmentState(
            KioskLifecycle.Pending, "K7QM-4XPT", DateTime.UtcNow.AddMinutes(30),
            "neoprint://activate/x", Guid.NewGuid(), null, []));

        model.Screen.Should().Be(KioskScreen.Activation);

        // Plan section 7.3: the activation screen takes the footer's place,
        // because there is no kiosk code to show yet.
        model.ShowKioskQr.Should().BeFalse();
        model.KioskQr.Should().BeNull();
    }

    [Fact]
    public void The_header_says_a_phone_joined_never_who_and_forgets_it_when_its_session_ends()
    {
        var model = new MainWindowViewModel();
        model.HasConnectedPhones.Should().BeFalse();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        model.SessionOpened(first, DateTime.UtcNow.AddMinutes(10));
        model.ConnectedPhonesText.Should().Be("Telefon qoşulub");

        model.SessionOpened(second, DateTime.UtcNow.AddMinutes(10));
        model.ConnectedPhonesText.Should().Be("2 telefon qoşulub");

        // The same session extended is still one phone.
        model.SessionOpened(first, DateTime.UtcNow.AddMinutes(10));
        model.ConnectedPhones.Should().Be(2);

        model.SessionClosed(second);
        model.ConnectedPhones.Should().Be(1);

        // Its "closed" message lost: past its end it goes anyway.
        model.SessionOpened(first, DateTime.UtcNow.AddSeconds(-1));
        model.Now = DateTime.Now;
        model.HasConnectedPhones.Should().BeFalse();
    }

    [Fact]
    public void After_a_restart_the_header_counts_the_phones_the_server_says_are_joined()
    {
        var model = new MainWindowViewModel();
        var heard = Guid.NewGuid();
        model.SessionOpened(heard, DateTime.UtcNow.AddMinutes(10));

        // What the server has replaces what this screen heard: the one heard has
        // since closed, two others are open and one is already past its end.
        model.ReplaceSessions(
        [
            (Guid.NewGuid(), DateTime.UtcNow.AddMinutes(9)),
            (Guid.NewGuid(), DateTime.UtcNow.AddMinutes(4)),
            (Guid.NewGuid(), DateTime.UtcNow.AddSeconds(-5)),
        ]);

        model.ConnectedPhones.Should().Be(2);
        model.ConnectedPhonesText.Should().Be("2 telefon qoşulub");

        model.ReplaceSessions([]);
        model.HasConnectedPhones.Should().BeFalse();
    }

    [Fact]
    public void A_detached_kiosk_shows_nothing_of_the_kiosk_it_was_on_the_activation_screen()
    {
        var model = new MainWindowViewModel();
        model.ApplyConfiguration(new KioskConfiguration(
            Guid.NewGuid(), "KSK-0042", "Old Library Kiosk", "Online", "az", null, new PrinterCapabilityFlags(true, true, true), [], null, 5, 50, 200, null,
            new Dictionary<string, PaperStockLevel>(), new Dictionary<string, bool>(), 15, 24, DateTime.UtcNow));
        model.KioskName.Should().Be("Old Library Kiosk");
        model.SessionOpened(Guid.NewGuid(), DateTime.UtcNow.AddMinutes(10));

        model.ForgetKiosk();
        model.Apply(new EnrollmentState(
            KioskLifecycle.Revoked, "K7QM-4XPT", DateTime.UtcNow.AddMinutes(30), "neoprint://activate/x", Guid.NewGuid(), null, []));

        model.Screen.Should().Be(KioskScreen.Activation);
        model.KioskName.Should().BeNull();
        model.KioskCode.Should().BeNull();
        model.HasConnectedPhones.Should().BeFalse();
        model.ShowKioskQr.Should().BeFalse();
    }

    [Fact]
    public void An_activated_kiosk_shows_its_qr_and_code_in_the_footer()
    {
        var model = new MainWindowViewModel();

        model.Apply(new EnrollmentState(
            KioskLifecycle.Active, null, null, null, Guid.NewGuid(), "KSK-0042", []));

        model.KioskCode.Should().Be("KSK-0042");
        model.ShowKioskQr.Should().BeTrue();

        // Plan section 2.9: the kiosk QR carries the code, and only ever
        // appears in the footer.
        model.KioskQr.Should().Be("neoprint://kiosk/KSK-0042");
    }

    [Fact]
    public void An_activated_kiosk_waits_on_the_not_configured_screen_until_a_printer_is_chosen()
    {
        var model = new MainWindowViewModel();

        model.Apply(new EnrollmentState(
            KioskLifecycle.Active, null, null, null, Guid.NewGuid(), "KSK-0042", []));

        // Plan section 7.7: activated is not the same as ready. A printer has
        // to be selected from the admin panel first.
        model.Screen.Should().Be(KioskScreen.NotConfigured);
    }

    [Fact]
    public void A_revoked_device_goes_back_to_the_activation_screen()
    {
        var model = new MainWindowViewModel();

        model.Apply(new EnrollmentState(
            KioskLifecycle.Active, null, null, null, Guid.NewGuid(), "KSK-0042", []));
        model.Apply(new EnrollmentState(
            KioskLifecycle.Revoked, null, null, null, null, null, []));

        model.Screen.Should().Be(KioskScreen.Activation);
    }

    [Fact]
    public void Switching_language_changes_the_text_on_the_shell_and_the_activation_screen()
    {
        var model = new MainWindowViewModel();

        model.Languages.Single(option => option.IsSelected).Code.Should().Be("az");

        var azerbaijani = model.QueueTitle;
        var azerbaijaniActivation = model.Activation.Title;

        model.SelectLanguageCommand.Execute("en");

        model.Language.Should().Be("en");
        model.QueueTitle.Should().NotBe(azerbaijani);
        model.Activation.Title.Should().NotBe(azerbaijaniActivation,
            "the activation screen follows the header's language picker");
        model.Languages.Where(option => option.IsSelected).Select(option => option.Code).Should().Equal(
            ["en"], "the header shows which language is on (T-086)");
    }

    [Fact]
    public void An_unsupported_language_is_ignored_rather_than_blanking_the_screen()
    {
        var model = new MainWindowViewModel();

        model.SelectLanguageCommand.Execute("de");

        model.Language.Should().Be("az");
    }

    [Fact]
    public void The_language_picker_offers_exactly_the_three_supported_languages()
    {
        var model = new MainWindowViewModel();

        model.Languages.Select(option => option.Code).Should().Equal("az", "en", "ru");
        model.Languages.Should().OnlyContain(option => option.Name.Length > 0);
    }

    [Fact]
    public void The_queue_is_trimmed_to_the_configured_display_limit()
    {
        var model = new MainWindowViewModel { QueueDisplayLimit = 3 };

        model.SetQueue(Enumerable.Range(1, 10).Select(index =>
            new QueueEntry(index, $"{index:D6}", "A", 2, IsPrinting: index == 1)));

        // Plan section 2.4: Queue.DisplayLimit is 3 or 5.
        model.Queue.Should().HaveCount(3);
        model.Queue[0].JobCode.Should().Be("000001");
    }

    [Fact]
    public void Replacing_the_queue_does_not_leave_the_previous_entries_behind()
    {
        var model = new MainWindowViewModel();

        model.SetQueue([new QueueEntry(1, "000001", "A", 1, false)]);
        model.SetQueue([new QueueEntry(1, "000002", "B", 1, false)]);

        model.Queue.Should().HaveCount(1);
        model.Queue[0].JobCode.Should().Be("000002");
    }
}

public sealed class ActivationViewModelTests
{
    [Fact]
    public void The_code_placeholder_keeps_the_layout_stable_before_a_code_arrives()
    {
        var model = new ActivationViewModel();

        // A blank space where the code goes would make the screen jump when it
        // appears.
        model.DisplayCode.Should().Be("····-····");
    }

    [Fact]
    public void The_countdown_reaches_zero_and_stops_there()
    {
        var model = new ActivationViewModel
        {
            PairingCodeExpiresAt = DateTime.UtcNow.AddSeconds(-30),
        };

        model.Tick();

        model.TimeUntilRefresh.Should().Be(TimeSpan.Zero, "a negative countdown must never be shown");
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(1800, "30:00")]
    public void The_countdown_is_formatted_as_minutes_and_seconds(int seconds, string expected)
    {
        ActivationViewModel.FormatCountdown(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void A_negative_countdown_formats_as_zero()
    {
        ActivationViewModel.FormatCountdown(TimeSpan.FromSeconds(-10)).Should().Be("0:00");
    }

    [Fact]
    public void The_printer_summary_says_so_when_no_printer_was_found()
    {
        var model = new ActivationViewModel();

        // A kiosk with no printer still has to be activatable; the admin needs
        // to see that none was detected (plan section 7.3).
        model.PrintersSummary.Should().Be(Strings.Get("az", "activation.noPrinters"));
    }

    [Fact]
    public void The_printer_summary_lists_what_was_found()
    {
        var model = new ActivationViewModel
        {
            Printers =
            [
                new DetectedPrinter("HP LaserJet", "hpcups", true, false, true, false),
                new DetectedPrinter("Canon iR", "canon", true, true, true, true),
            ],
        };

        model.PrintersSummary.Should().Be("HP LaserJet, Canon iR");
    }

    [Fact]
    public void The_connection_status_reflects_whether_the_backend_answers()
    {
        var model = new ActivationViewModel();

        var disconnected = model.ConnectionStatus;

        model.IsBackendReachable = true;

        // A kiosk that has just been plugged in shows why it is not activating
        // yet, rather than sitting silently (plan section 7.3).
        model.ConnectionStatus.Should().NotBe(disconnected);
    }

    [Fact]
    public void A_revoked_device_explains_itself_instead_of_showing_a_countdown()
    {
        var model = new ActivationViewModel { Lifecycle = KioskLifecycle.Revoked };

        model.CodeExpiryText.Should().Be(Strings.Get("az", "activation.revoked"));
    }
}

public sealed class LocalizationTests
{
    [Fact]
    public void Every_key_is_translated_into_every_supported_language()
    {
        foreach (var language in Strings.SupportedLanguages)
        {
            foreach (var key in Strings.AllKeys)
            {
                var value = Strings.Get(language, key);

                value.Should().NotBe(key,
                    $"'{key}' has no {language} translation, so the UI would show the key itself");
                value.Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public void Azerbaijani_is_the_default()
    {
        Strings.DefaultLanguage.Should().Be("az");
        Strings.SupportedLanguages[0].Should().Be("az");
    }

    [Fact]
    public void An_unknown_language_falls_back_to_the_default_rather_than_showing_nothing()
    {
        Strings.Get("de", "activation.title").Should().Be(Strings.Get("az", "activation.title"));
    }

    [Fact]
    public void An_unknown_key_shows_the_key_so_the_gap_is_visible()
    {
        // Better a visible key than a blank screen: a missing string gets
        // noticed rather than shipped.
        Strings.Get("az", "nothing.here").Should().Be("nothing.here");
    }

    [Fact]
    public void Every_supported_language_has_a_display_name_for_the_picker()
    {
        foreach (var language in Strings.SupportedLanguages)
        {
            Strings.LanguageNames.Should().ContainKey(language);
            Strings.LanguageNames[language].Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("az", true)]
    [InlineData("en", true)]
    [InlineData("ru", true)]
    [InlineData("de", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Language_support_is_reported_accurately(string? language, bool expected)
    {
        Strings.IsSupported(language).Should().Be(expected);
    }
}
