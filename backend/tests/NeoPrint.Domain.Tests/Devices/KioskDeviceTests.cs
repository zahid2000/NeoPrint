using NeoPrint.Domain.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Tests.Devices;

public sealed class KioskDeviceTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    private static KioskDevice Pending() => new()
    {
        PublicKey = "spki-base64",
        PairingCodeHash = "hash",
        PairingCodeExpiresAt = Now.AddMinutes(30),
    };

    [Fact]
    public void Activating_binds_the_device_to_a_kiosk_and_burns_the_pairing_code()
    {
        var device = Pending();
        var kioskId = Guid.NewGuid();
        var admin = Guid.NewGuid();

        device.Activate(kioskId, ActorContext.Admin(admin), Now);

        device.Status.Should().Be(DeviceStatus.Active);
        device.KioskId.Should().Be(kioskId);
        device.ActivatedBy.Should().Be(admin);
        device.ActivatedAt.Should().Be(Now);
        device.PairingCodeHash.Should().BeNull("a pairing code is single use");
        device.PairingCodeExpiresAt.Should().BeNull();
    }

    [Fact]
    public void Activating_the_same_device_onto_the_same_kiosk_twice_is_harmless()
    {
        var device = Pending();
        var kioskId = Guid.NewGuid();
        device.Activate(kioskId, ActorContext.Admin(Guid.NewGuid()), Now);
        var activatedAt = device.ActivatedAt;

        var act = () => device.Activate(kioskId, ActorContext.Admin(Guid.NewGuid()), Now.AddMinutes(5));

        act.Should().NotThrow("an admin may well press the button twice");
        device.ActivatedAt.Should().Be(activatedAt);
    }

    [Fact]
    public void A_revoked_device_cannot_be_activated_again()
    {
        var device = Pending();
        device.Revoke(ActorContext.Admin(Guid.NewGuid(), "hardware replaced"), Now);

        var act = () => device.Activate(Guid.NewGuid(), ActorContext.Admin(Guid.NewGuid()), Now);

        act.Should().Throw<DomainException>().WithMessage("*revoked*");
    }

    [Fact]
    public void Revoking_detaches_the_device_and_records_why()
    {
        var device = Pending();
        device.Activate(Guid.NewGuid(), ActorContext.Admin(Guid.NewGuid()), Now);
        var admin = Guid.NewGuid();

        device.Revoke(ActorContext.Admin(admin, "kiosk decommissioned"), Now.AddDays(1));

        device.Status.Should().Be(DeviceStatus.Revoked);
        device.KioskId.Should().BeNull();
        device.RevokedBy.Should().Be(admin);
        device.RevokeReason.Should().Be("kiosk decommissioned");
        device.CanIssueKioskToken.Should().BeFalse();
    }

    [Fact]
    public void Only_an_active_device_bound_to_a_kiosk_may_hold_a_kiosk_token()
    {
        var device = Pending();
        device.CanIssueKioskToken.Should().BeFalse("a pending device gets a provisioning token only");

        device.Activate(Guid.NewGuid(), ActorContext.System(), Now);
        device.CanIssueKioskToken.Should().BeTrue();
    }

    [Fact]
    public void A_pairing_code_expires()
    {
        var device = Pending();

        device.IsPairingCodeValid(Now.AddMinutes(29)).Should().BeTrue();
        device.IsPairingCodeValid(Now.AddMinutes(31)).Should().BeFalse();
    }

    [Fact]
    public void Pairing_codes_are_shaped_so_they_can_be_read_out_loud()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = KioskDevice.NewPairingCode();

            code.Should().MatchRegex("^[A-Z2-9]{4}-[A-Z2-9]{4}$");
            // No 0/O or 1/I/L: a code read off a kiosk screen must not be
            // mistyped (plan section 7.11).
            code.Should().NotContainAny("0", "O", "1", "I", "L");
        }
    }

    [Fact]
    public void Pairing_codes_are_compared_without_the_separator_and_without_case()
    {
        var normalized = KioskDevice.NormalizePairingCode("k7qm-4xpt");

        normalized.Should().Be("K7QM4XPT");
        KioskDevice.NormalizePairingCode("K7QM4XPT").Should().Be(normalized);
    }

    [Fact]
    public void Generated_pairing_codes_do_not_repeat_in_practice()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => KioskDevice.NewPairingCode()).ToList();

        codes.Should().OnlyHaveUniqueItems();
    }
}

public sealed class DeviceFingerprintTests
{
    [Fact]
    public void The_machine_id_decides_when_both_sides_have_one()
    {
        var before = new DeviceFingerprint { MachineId = "abc", Hostname = "kiosk-01" };
        var after = new DeviceFingerprint { MachineId = "abc", Hostname = "renamed" };

        before.LooksLikeSameMachine(after).Should().BeTrue();
    }

    [Fact]
    public void A_changed_machine_id_is_noticed()
    {
        var before = new DeviceFingerprint { MachineId = "abc" };
        var after = new DeviceFingerprint { MachineId = "xyz" };

        before.LooksLikeSameMachine(after).Should().BeFalse();
    }

    [Fact]
    public void Without_machine_ids_the_hostname_and_a_shared_mac_are_used()
    {
        var before = new DeviceFingerprint { Hostname = "kiosk-01", MacAddresses = ["AA:BB:CC:DD:EE:FF"] };
        var same = new DeviceFingerprint { Hostname = "KIOSK-01", MacAddresses = ["aa:bb:cc:dd:ee:ff", "11:22:33:44:55:66"] };
        var other = new DeviceFingerprint { Hostname = "kiosk-01", MacAddresses = ["11:22:33:44:55:66"] };

        before.LooksLikeSameMachine(same).Should().BeTrue();
        before.LooksLikeSameMachine(other).Should().BeFalse("the network card was swapped");
    }
}

public sealed class EnrollmentTokenTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_fresh_token_is_usable()
    {
        var token = new EnrollmentToken { ExpiresAt = Now.AddHours(72) };

        token.IsUsable(Now).Should().BeTrue();
    }

    [Fact]
    public void A_token_is_single_use()
    {
        var token = new EnrollmentToken { ExpiresAt = Now.AddHours(72), UsedByDeviceId = Guid.NewGuid() };

        token.IsUsable(Now).Should().BeFalse();
    }

    [Fact]
    public void An_expired_token_falls_back_to_the_pairing_code_flow()
    {
        var token = new EnrollmentToken { ExpiresAt = Now.AddHours(-1) };

        token.IsUsable(Now).Should().BeFalse();
    }

    [Fact]
    public void A_revoked_token_is_not_usable()
    {
        var token = new EnrollmentToken { ExpiresAt = Now.AddHours(72), RevokedAt = Now };

        token.IsUsable(Now).Should().BeFalse();
    }

    [Fact]
    public void Generated_tokens_are_long_and_unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => EnrollmentToken.NewToken()).ToList();

        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().OnlyContain(token => token.Length == 64);
    }
}

public sealed class KioskTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ActorContext Admin = ActorContext.Admin(Guid.NewGuid());

    [Fact]
    public void Binding_a_device_leaves_the_kiosk_unconfigured_until_a_printer_is_chosen()
    {
        var kiosk = new Kiosk { Code = "KSK-0001" };

        kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Linux, "0.1.0", Admin, Now);

        kiosk.Status.Should().Be(KioskStatus.NotConfigured);
        kiosk.Platform.Should().Be(KioskPlatform.Linux);
    }

    [Fact]
    public void Binding_a_replacement_device_to_a_configured_kiosk_brings_it_straight_online()
    {
        var kiosk = new Kiosk
        {
            Code = "KSK-0001",
            SelectedPrinter = new SelectedPrinter { Name = "HP LaserJet" },
        };

        kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Windows, "0.1.0", Admin, Now);

        kiosk.Status.Should().Be(KioskStatus.Online,
            "replacing broken hardware must not require reconfiguring the kiosk");
    }

    [Fact]
    public void A_disabled_kiosk_refuses_a_device()
    {
        var kiosk = new Kiosk { Code = "KSK-0001" };
        kiosk.MoveTo(KioskStatus.Disabled, Admin, Now);

        var act = () => kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Linux, "0.1.0", Admin, Now);

        act.Should().Throw<DomainException>().WithMessage("*disabled*");
    }

    [Fact]
    public void Unbinding_keeps_the_code_and_the_configuration()
    {
        var kiosk = new Kiosk
        {
            Code = "KSK-0007",
            SelectedPrinter = new SelectedPrinter { Name = "HP LaserJet" },
            PaperStock = { ["A4"] = new PaperStock { Capacity = 500, Current = 320 } },
        };
        kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Linux, "0.1.0", Admin, Now);

        kiosk.UnbindDevice(Admin, Now);

        kiosk.Status.Should().Be(KioskStatus.NotActivated);
        kiosk.ActiveDeviceId.Should().BeNull();
        kiosk.Code.Should().Be("KSK-0007");
        kiosk.SelectedPrinter!.Name.Should().Be("HP LaserJet");
        kiosk.PaperStock["A4"].Current.Should().Be(320);
    }

    [Fact]
    public void Effective_capabilities_come_from_the_selected_printer()
    {
        var kiosk = new Kiosk
        {
            AvailablePrinters =
            [
                new() { Name = "Mono", DetectedCapabilities = new PrinterCapabilities { Duplex = true } },
                new() { Name = "Colour", DetectedCapabilities = new PrinterCapabilities { Color = true, A3 = true } },
            ],
            SelectedPrinter = new SelectedPrinter { Name = "Colour" },
        };

        var capabilities = kiosk.EffectiveCapabilities();

        capabilities.Color.Should().BeTrue();
        capabilities.A3.Should().BeTrue();
        capabilities.Duplex.Should().BeFalse();
    }

    [Fact]
    public void An_admin_override_wins_over_what_was_detected()
    {
        var kiosk = new Kiosk
        {
            AvailablePrinters = [new() { Name = "P", DetectedCapabilities = new PrinterCapabilities { Color = true } }],
            SelectedPrinter = new SelectedPrinter { Name = "P" },
            // The driver claims colour, but the machine has no colour toner.
            CapabilityOverrides = new CapabilityOverrides { Color = false, Duplex = true },
        };

        var capabilities = kiosk.EffectiveCapabilities();

        capabilities.Color.Should().BeFalse();
        capabilities.Duplex.Should().BeTrue();
    }

    [Fact]
    public void A_kiosk_without_a_selected_printer_can_do_nothing()
    {
        var kiosk = new Kiosk
        {
            AvailablePrinters = [new() { Name = "P", DetectedCapabilities = new PrinterCapabilities { Color = true } }],
        };

        var capabilities = kiosk.EffectiveCapabilities();

        capabilities.Color.Should().BeFalse();
        capabilities.Duplex.Should().BeFalse();
        capabilities.A3.Should().BeFalse();
    }

    [Fact]
    public void A_selected_printer_that_has_disappeared_leaves_no_capabilities()
    {
        var kiosk = new Kiosk
        {
            AvailablePrinters = [new() { Name = "Present" }],
            SelectedPrinter = new SelectedPrinter { Name = "Removed" },
        };

        kiosk.EffectiveCapabilities().Color.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, "KSK-0001")]
    [InlineData(42, "KSK-0042")]
    [InlineData(1234, "KSK-1234")]
    public void Kiosk_codes_are_zero_padded(int sequence, string expected)
    {
        Kiosk.NextCode(sequence).Should().Be(expected);
    }
}
