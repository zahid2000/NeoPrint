using NeoPrint.Crypto;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.Platform.Linux;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The lockdown and the only way out of it (plan sections 7.1, 7.12; step 3
/// DoD: "kiosk tətbiqindən OS-ə çıxmaq mümkün deyil (admin PIN istisna)").
/// </summary>
[Collection(KioskLanguageCollection.Name)]
public sealed class LockdownTests
{
    private static readonly string PinHash = SecretHash.Hash("135790");

    private static DateTime _now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static AdminMenuViewModel Admin(string? hash = null) => new(() => hash ?? PinHash, () => _now) { Language = "az" };

    private static void Type(AdminMenuViewModel admin, string pin)
    {
        foreach (var digit in pin)
        {
            admin.DigitCommand.Execute(digit.ToString());
        }

        admin.EnterCommand.Execute(null);
    }

    [Theory]
    [InlineData(KeyboardLockdownRules.LeftWindows, false, false, true)]
    [InlineData(KeyboardLockdownRules.RightWindows, false, false, true)]
    [InlineData(KeyboardLockdownRules.Tab, true, false, true)]
    [InlineData(KeyboardLockdownRules.F4, true, false, true)]
    [InlineData(KeyboardLockdownRules.Escape, true, false, true)]
    [InlineData(KeyboardLockdownRules.Escape, false, true, true)]
    [InlineData(KeyboardLockdownRules.Tab, false, false, false)]
    [InlineData(0x41, false, false, false)]
    [InlineData(KeyboardLockdownRules.F4, false, false, false)]
    public void The_escape_combinations_are_swallowed_and_typing_is_not(int key, bool alt, bool control, bool blocked)
        => KeyboardLockdownRules.IsBlocked(key, alt, control).Should().Be(blocked);

    [Fact]
    public void Five_taps_on_the_logo_within_three_seconds_open_the_pin_screen_and_four_do_not()
    {
        var shell = new MainWindowViewModel { Admin = Admin(), Screen = KioskScreen.Home };
        var start = DateTime.UtcNow;

        for (var tap = 0; tap < 4; tap++)
        {
            shell.AdminTap(start.AddMilliseconds(tap * 300));
        }

        shell.Screen.Should().Be(KioskScreen.Home);

        // Too slow: the first tap fell out of the window.
        shell.AdminTap(start.AddSeconds(4));
        shell.Screen.Should().Be(KioskScreen.Home);

        for (var tap = 0; tap < 5; tap++)
        {
            shell.AdminTap(start.AddSeconds(10).AddMilliseconds(tap * 300));
        }

        shell.Screen.Should().Be(KioskScreen.Admin);
        shell.Admin!.Stage.Should().Be(AdminStage.Pin);

        shell.Admin.CloseCommand.Execute(null);
        shell.CloseAdmin();
        shell.Screen.Should().Be(KioskScreen.Home, "closing goes back to where the kiosk was");
    }

    [Fact]
    public void The_right_pin_opens_the_menu_and_the_digits_are_never_shown()
    {
        var admin = Admin();
        admin.Open(safeMode: false);

        foreach (var digit in "1357")
        {
            admin.DigitCommand.Execute(digit.ToString());
        }

        admin.PinDots.Should().Be("●●●●");
        admin.EraseCommand.Execute(null);
        admin.PinDots.Should().Be("●●●");

        admin.PinInput = string.Empty;
        Type(admin, "135790");

        admin.Stage.Should().Be(AdminStage.Menu);
        admin.PinInput.Should().BeEmpty("the PIN is not kept once it was checked");
    }

    [Fact]
    public void Five_wrong_pins_lock_the_menu_for_five_minutes()
    {
        var admin = Admin();
        admin.Open(safeMode: false);

        for (var attempt = 1; attempt < AdminMenuViewModel.MaxAttempts; attempt++)
        {
            Type(admin, "000000");
            admin.Error.Should().Be(Localization.Strings.Get("az", "admin.wrongPin", AdminMenuViewModel.MaxAttempts - attempt));
        }

        Type(admin, "000000");
        admin.Stage.Should().Be(AdminStage.Locked);

        // Even the right PIN does nothing while it is locked, reopened or not.
        admin.Open(safeMode: false);
        admin.Stage.Should().Be(AdminStage.Locked);
        admin.DigitCommand.Execute("1");
        admin.PinInput.Should().BeEmpty();

        _now += AdminMenuViewModel.LockTime + TimeSpan.FromSeconds(1);
        admin.Open(safeMode: false);
        Type(admin, "135790");
        admin.Stage.Should().Be(AdminStage.Menu);
    }

    [Fact]
    public void A_kiosk_without_a_pin_has_no_way_in_from_the_screen()
    {
        var admin = new AdminMenuViewModel(() => null) { Language = "az" };

        admin.Open(safeMode: false);

        admin.Stage.Should().Be(AdminStage.NoPin);
        admin.DigitCommand.Execute("1");
        admin.EnterCommand.Execute(null);
        admin.Stage.Should().Be(AdminStage.NoPin);
    }

    [Fact]
    public void The_actions_run_only_after_the_pin_and_leaving_safe_mode_only_in_safe_mode()
    {
        var admin = Admin();
        var asked = new List<string>();
        admin.LeaveSafeModeRequested += () => asked.Add("safe");
        admin.RestartRequested += () => asked.Add("restart");
        admin.MaintenanceRequested += () => asked.Add("maintenance");

        admin.Open(safeMode: false);
        admin.RestartCommand.Execute(null);
        admin.MaintenanceCommand.Execute(null);
        asked.Should().BeEmpty("nothing happens before the PIN");

        Type(admin, "135790");
        admin.LeaveSafeModeCommand.Execute(null);
        admin.RestartCommand.Execute(null);
        admin.MaintenanceCommand.Execute(null);

        asked.Should().Equal("restart", "maintenance");

        admin.Open(safeMode: true);
        Type(admin, "135790");
        admin.LeaveSafeModeCommand.Execute(null);
        asked.Should().EndWith("safe");
    }

    [Fact]
    public void Lsblk_output_names_the_sticks_to_mount_and_nothing_else()
    {
        const string output = """
            {
               "blockdevices": [
                  {"name":"/dev/sda", "rm":false, "hotplug":false, "type":"disk", "fstype":null, "mountpoints":[null],
                     "children": [
                        {"name":"/dev/sda1", "rm":false, "hotplug":false, "type":"part", "fstype":"ext4", "mountpoints":["/"]},
                        {"name":"/dev/sda2", "rm":false, "hotplug":false, "type":"part", "fstype":"ext4", "mountpoints":[null]}
                     ]
                  },
                  {"name":"/dev/sdb", "rm":true, "hotplug":true, "type":"disk", "fstype":null, "mountpoints":[null],
                     "children": [
                        {"name":"/dev/sdb1", "rm":true, "hotplug":true, "type":"part", "fstype":"vfat", "mountpoints":[null]},
                        {"name":"/dev/sdb2", "rm":true, "hotplug":true, "type":"part", "fstype":"crypto_LUKS", "mountpoints":[null]}
                     ]
                  },
                  {"name":"/dev/sdc", "rm":"1", "hotplug":"1", "type":"disk", "fstype":"exfat", "mountpoint":null},
                  {"name":"/dev/sdd", "rm":true, "hotplug":true, "type":"disk", "fstype":null, "mountpoints":[null],
                     "children": [
                        {"name":"/dev/sdd1", "rm":true, "hotplug":true, "type":"part", "fstype":"vfat", "mountpoints":["/media/kiosk/STICK"]}
                     ]
                  }
               ]
            }
            """;

        RemovableBlockDevices.UnmountedRemovable(output).Should().Equal("/dev/sdb1", "/dev/sdc");
        RemovableBlockDevices.UnmountedRemovable("not json").Should().BeEmpty();
    }

    [Fact]
    public void A_provisioned_kiosk_runs_converters_through_the_sandbox_wrapper_as_its_own_user()
        => LinuxConverterRunner.Sandbox("neoprint-convert", 60, "/var/lib/neoprint/spool/work/abc", "soffice", ["--headless", "--convert-to", "pdf", "in.docx"])
            .Should().Equal(
                "-n", "-u", "neoprint-convert", LinuxConverterRunner.SandboxWrapper, "60", "/var/lib/neoprint/spool/work/abc",
                "soffice", "--headless", "--convert-to", "pdf", "in.docx");
}
