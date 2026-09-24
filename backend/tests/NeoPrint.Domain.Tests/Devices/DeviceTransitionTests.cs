using NeoPrint.Domain.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;

namespace NeoPrint.Domain.Tests.Devices;

/// <summary>
/// The device and kiosk state machines, against
/// <c>docs/STATE_MACHINES.md</c>.
/// </summary>
public sealed class DeviceTransitionTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ActorContext Admin =
        ActorContext.Admin(Guid.NewGuid(), correlationId: "corr-device");

    private static KioskDevice Pending() => new()
    {
        PublicKey = "spki-base64",
        PairingCodeHash = "hash",
        PairingCodeExpiresAt = Now.AddMinutes(30),
    };

    // -----------------------------------------------------------------------
    // Device: Pending → Active, Active → Revoked, Pending → Expired
    // -----------------------------------------------------------------------

    [Fact]
    public void Activating_records_a_transition_with_the_actor()
    {
        var device = Pending();

        device.Activate(Guid.NewGuid(), Admin, Now);

        var transition = device.PendingTransitions.Should().ContainSingle().Subject;

        transition.EntityType.Should().Be("KioskDevice");
        transition.EntityId.Should().Be(device.Id);
        transition.FromStatus.Should().Be("Pending");
        transition.ToStatus.Should().Be("Active");
        transition.ActorType.Should().Be(ActorType.Admin);
        transition.CorrelationId.Should().Be("corr-device");
    }

    [Fact]
    public void Revoking_records_the_reason()
    {
        var device = Pending();
        device.Activate(Guid.NewGuid(), Admin, Now);
        device.DrainTransitions();

        device.Revoke(Admin.Because("hardware replaced"), Now.AddHours(1));

        var transition = device.PendingTransitions.Should().ContainSingle().Subject;

        transition.FromStatus.Should().Be("Active");
        transition.ToStatus.Should().Be("Revoked");
        transition.Reason.Should().Be("hardware replaced");
    }

    [Fact]
    public void Revoked_is_a_sink()
    {
        // A revoked device must never be accepted again; reinstalling makes a
        // new key, which registers as a new device.
        KioskDevice.Transitions.IsAllowed(DeviceStatus.Revoked, DeviceStatus.Active).Should().BeFalse();
        KioskDevice.Transitions.IsAllowed(DeviceStatus.Revoked, DeviceStatus.Pending).Should().BeFalse();
        KioskDevice.Transitions.IsAllowed(DeviceStatus.Revoked, DeviceStatus.Expired).Should().BeFalse();
    }

    [Fact]
    public void Revoking_twice_records_one_transition()
    {
        var device = Pending();
        device.Activate(Guid.NewGuid(), Admin, Now);
        device.DrainTransitions();

        device.Revoke(Admin, Now.AddHours(1));
        device.Revoke(Admin, Now.AddHours(2));

        // The panel may send the command twice; the device is no less revoked
        // for it (docs/STATE_MACHINES.md: transitions are idempotent).
        device.PendingTransitions.Should().ContainSingle();
    }

    [Fact]
    public void A_device_nobody_activated_expires_and_its_code_stops_looking_valid()
    {
        var device = Pending();

        device.Expire(ActorContext.System("Pending window elapsed"), Now.AddDays(1));

        device.Status.Should().Be(DeviceStatus.Expired);
        device.IsPairingCodeValid(Now.AddDays(1)).Should().BeFalse();

        device.PendingTransitions.Should().ContainSingle()
            .Which.ActorType.Should().Be(ActorType.System);
    }

    [Fact]
    public void An_expired_device_may_ask_for_a_new_code()
    {
        var device = Pending();
        device.Expire(ActorContext.System(), Now.AddDays(1));
        device.DrainTransitions();

        // It did nothing wrong - nobody got round to activating it.
        device.AwaitActivation("new-hash", Now.AddDays(1).AddMinutes(30), ActorContext.System(), Now.AddDays(1));

        device.Status.Should().Be(DeviceStatus.Pending);
        device.PairingCodeHash.Should().Be("new-hash");
        device.PendingTransitions.Should().ContainSingle()
            .Which.FromStatus.Should().Be("Expired");
    }

    [Fact]
    public void A_revoked_device_cannot_be_put_back_on_the_activation_screen()
    {
        var device = Pending();
        device.Revoke(Admin, Now);

        var act = () => device.AwaitActivation("hash", Now.AddMinutes(30), Admin, Now);

        act.Should().Throw<IllegalStateTransitionException>();
    }

    [Fact]
    public void Draining_transitions_leaves_none_behind()
    {
        var device = Pending();
        device.Activate(Guid.NewGuid(), Admin, Now);

        device.DrainTransitions().Should().ContainSingle();

        // A second save must not write the same transition again.
        device.DrainTransitions().Should().BeEmpty();
        device.PendingTransitions.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Kiosk
    // -----------------------------------------------------------------------

    [Fact]
    public void Binding_a_device_records_where_the_kiosk_came_from()
    {
        var kiosk = new Kiosk { Code = "KSK-0001" };

        kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Linux, "0.1.0", Admin, Now);

        var transition = kiosk.PendingTransitions.Should().ContainSingle().Subject;

        transition.EntityType.Should().Be("Kiosk");
        transition.FromStatus.Should().Be("NotActivated");
        transition.ToStatus.Should().Be("NotConfigured");
    }

    [Fact]
    public void Unbinding_an_already_unbound_kiosk_records_nothing()
    {
        var kiosk = new Kiosk { Code = "KSK-0001" };

        kiosk.UnbindDevice(Admin, Now);

        kiosk.PendingTransitions.Should().BeEmpty();
    }

    [Fact]
    public void A_kiosk_can_be_paused_and_resumed()
    {
        var kiosk = new Kiosk
        {
            Code = "KSK-0001",
            SelectedPrinter = new SelectedPrinter { Name = "HP LaserJet" },
        };

        kiosk.BindDevice(Guid.NewGuid(), KioskPlatform.Linux, "0.1.0", Admin, Now);
        kiosk.Status.Should().Be(KioskStatus.Online);

        kiosk.MoveTo(KioskStatus.Paused, Admin.Because("Stocktake"), Now.AddHours(1));
        kiosk.Status.Should().Be(KioskStatus.Paused);

        kiosk.MoveTo(KioskStatus.Online, Admin, Now.AddHours(2));
        kiosk.Status.Should().Be(KioskStatus.Online);

        kiosk.PendingTransitions.Should().HaveCount(3);
    }

    [Fact]
    public void A_kiosk_cannot_jump_from_not_activated_to_paused()
    {
        var kiosk = new Kiosk { Code = "KSK-0001" };

        var act = () => kiosk.MoveTo(KioskStatus.Paused, Admin, Now);

        // Pausing a kiosk that has no device is meaningless.
        act.Should().Throw<IllegalStateTransitionException>().WithMessage("*NotActivated*Paused*");
    }

    [Fact]
    public void Any_kiosk_can_be_taken_out_of_service()
    {
        foreach (var status in new[] { KioskStatus.NotActivated, KioskStatus.NotConfigured, KioskStatus.Online })
        {
            Kiosk.Transitions.IsAllowed(status, KioskStatus.Disabled).Should().BeTrue();
        }
    }

    [Fact]
    public void Losing_its_device_returns_a_kiosk_to_not_activated_from_anywhere()
    {
        foreach (var status in new[]
                 {
                     KioskStatus.NotConfigured, KioskStatus.Online, KioskStatus.Paused,
                     KioskStatus.Offline, KioskStatus.Maintenance, KioskStatus.Disabled,
                 })
        {
            Kiosk.Transitions.IsAllowed(status, KioskStatus.NotActivated)
                .Should().BeTrue($"a kiosk that was {status} still loses its device");
        }
    }
}
