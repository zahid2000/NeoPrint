using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Kiosks;

/// <summary>What an admin can ask a kiosk to do (plan section 7.8).</summary>
/// <remarks>
/// Step 3 delivers the printer commands; the rest of the list arrives with
/// remote operations and updates in step 7.
/// </remarks>
public enum KioskCommandType
{
    /// <summary>Discover the printers again and report them.</summary>
    RefreshPrinters = 0,

    /// <summary>Print the built-in test page on the selected printer (plan section 7.7).</summary>
    TestPrint = 1,

    /// <summary>Stop taking new orders; the queue is finished, the screen says "maintenance" (plan section 7.8).</summary>
    Pause = 2,

    /// <summary>Take orders again.</summary>
    Resume = 3,

    /// <summary>Restart the kiosk application once the job at the printer is done.</summary>
    RestartApp = 4,

    /// <summary>Restart the machine once the job at the printer is done; the agent does it.</summary>
    Reboot = 5,
}

/// <summary>Where a command stands.</summary>
public enum KioskCommandStatus
{
    /// <summary>Stored and pushed; the kiosk picks it up now or when it next connects.</summary>
    Sent = 0,
    Succeeded = 1,
    Failed = 2,

    /// <summary>Nobody answered in 24 hours (plan section 7.8).</summary>
    Expired = 3,

    /// <summary>
    /// The kiosk has it and will carry it out, but cannot say "done" yet: a
    /// restart is answered by the application that comes up afterwards.
    /// </summary>
    Acknowledged = 4,
}

/// <summary>
/// A command for one kiosk (plan section 5, <c>kiosk_commands</c>; 7.8).
/// </summary>
/// <remarks>
/// Written before it is pushed over <c>/hubs/kiosk</c>, so a kiosk that was
/// offline gets it from <c>GET kiosks/me/commands</c> when it comes back.
/// </remarks>
public sealed class KioskCommand : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Guid KioskId { get; set; }

    public KioskCommandType Type { get; set; }

    public Guid? CreatedBy { get; set; }

    public KioskCommandStatus Status { get; private set; } = KioskCommandStatus.Sent;

    /// <summary>What the kiosk said, as a stored message (<see cref="UserMessage.Encode"/>).</summary>
    public string? Result { get; private set; }

    public DateTime? CompletedAt { get; private set; }

    public static readonly StateMachine<KioskCommandStatus> Transitions =
        new StateMachine<KioskCommandStatus>(nameof(KioskCommand))
            .Allow(
                KioskCommandStatus.Sent,
                KioskCommandStatus.Acknowledged,
                KioskCommandStatus.Succeeded,
                KioskCommandStatus.Failed,
                KioskCommandStatus.Expired)
            .Allow(KioskCommandStatus.Acknowledged, KioskCommandStatus.Succeeded, KioskCommandStatus.Failed, KioskCommandStatus.Expired);

    /// <summary>Not answered yet: the kiosk still owes its result.</summary>
    public bool IsOpen => Status is KioskCommandStatus.Sent or KioskCommandStatus.Acknowledged;

    /// <summary>
    /// The kiosk says it has the command and will carry it out. Saying so
    /// twice - a repeated request - changes nothing.
    /// </summary>
    public void Acknowledge(ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (Status != KioskCommandStatus.Sent)
        {
            return;
        }

        Record(Transitions.Transition(Id, Status, KioskCommandStatus.Acknowledged, actor, now));
        Status = KioskCommandStatus.Acknowledged;
    }

    /// <summary>Whether the 24 hours have passed without an answer.</summary>
    public bool IsOverdue(DateTime now) => IsOpen && now - CreatedAt > Lifetime;

    /// <summary>The kiosk's answer. The first answer counts; a repeat changes nothing.</summary>
    public void Complete(bool succeeded, UserMessage? result, ActorContext actor, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!IsOpen)
        {
            return;
        }

        var next = succeeded ? KioskCommandStatus.Succeeded : KioskCommandStatus.Failed;
        Record(Transitions.Transition(Id, Status, next, actor, now));

        Status = next;
        Result = result?.Encode();
        CompletedAt = now;
    }

    public void Expire(DateTime now)
    {
        if (!IsOpen)
        {
            return;
        }

        Record(Transitions.Transition(Id, Status, KioskCommandStatus.Expired, ActorContext.System("kioskCommands.reason.expired"), now));
        Status = KioskCommandStatus.Expired;
        CompletedAt = now;
    }
}
