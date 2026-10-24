using System.Security.Cryptography;

using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Releases;

/// <summary>Which kiosks a rollout reaches (plan section 5, <c>release_rollouts.Target</c>).</summary>
public enum RolloutTarget
{
    /// <summary>Every kiosk of the release's platform.</summary>
    All = 0,

    /// <summary>The kiosks listed.</summary>
    Kiosks = 1,

    /// <summary>A fixed share of the kiosks, the same ones each time it is asked.</summary>
    Percent = 2,

    /// <summary>The kiosks on one update channel.</summary>
    Channel = 3,
}

public enum RolloutStatus
{
    Active = 0,
    Paused = 1,

    /// <summary>Done: the release stays installed, the rollout offers nothing more.</summary>
    Completed = 2,

    /// <summary>Taken back: the kiosks it reached are offered the release before it again.</summary>
    RolledBack = 3,
}

/// <summary>
/// Offers one release to a set of kiosks (plan section 7.12, step 2). A kiosk
/// asks <c>GET kiosks/me/update</c> and is offered the newest release of each
/// component that an active rollout targets it with.
/// </summary>
public sealed class ReleaseRollout : Entity
{
    public static readonly StateMachine<RolloutStatus> Transitions =
        new StateMachine<RolloutStatus>(nameof(ReleaseRollout))
            .Allow(RolloutStatus.Active, RolloutStatus.Paused, RolloutStatus.Completed, RolloutStatus.RolledBack)
            .Allow(RolloutStatus.Paused, RolloutStatus.Active, RolloutStatus.Completed, RolloutStatus.RolledBack);

    public Guid ReleaseId { get; set; }

    // Copied from the release, so a kiosk's question is answered without
    // reading every release.
    public ReleaseComponent Component { get; set; }

    public string Platform { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public RolloutTarget Target { get; set; }

    /// <summary>For <see cref="RolloutTarget.Kiosks"/>.</summary>
    public List<Guid> KioskIds { get; set; } = [];

    /// <summary>For <see cref="RolloutTarget.Percent"/>: 1-100.</summary>
    public int? Percent { get; set; }

    /// <summary>For <see cref="RolloutTarget.Channel"/>.</summary>
    public ReleaseChannel? Channel { get; set; }

    public RolloutStatus Status { get; private set; } = RolloutStatus.Active;

    public Guid? CreatedBy { get; set; }

    public DateTime? EndedAt { get; private set; }

    /// <summary>Whether the rollout still offers its release.</summary>
    public bool IsOffering => Status == RolloutStatus.Active;

    /// <summary>Whether this rollout's target takes in a kiosk on the given channel.</summary>
    public bool Reaches(Guid kioskId, ReleaseChannel kioskChannel) => Target switch
    {
        RolloutTarget.All => true,
        RolloutTarget.Kiosks => KioskIds.Contains(kioskId),
        RolloutTarget.Channel => Channel == kioskChannel,
        RolloutTarget.Percent => Bucket(Id, kioskId) < (Percent ?? 0),
        _ => false,
    };

    public void Pause(ActorContext actor, DateTime now) => MoveTo(RolloutStatus.Paused, actor, now);

    public void Resume(ActorContext actor, DateTime now) => MoveTo(RolloutStatus.Active, actor, now);

    public void Complete(ActorContext actor, DateTime now) => MoveTo(RolloutStatus.Completed, actor, now);

    public void RollBack(ActorContext actor, DateTime now) => MoveTo(RolloutStatus.RolledBack, actor, now);

    /// <summary>
    /// The kiosk's place 0-99 in this rollout: the same kiosk always lands in
    /// the same place, so raising a 10 % rollout to 30 % keeps the first 10 %.
    /// </summary>
    public static int Bucket(Guid rolloutId, Guid kioskId)
    {
        Span<byte> input = stackalloc byte[32];
        rolloutId.TryWriteBytes(input[..16]);
        kioskId.TryWriteBytes(input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return (int)(BitConverter.ToUInt32(hash[..4]) % 100);
    }

    private void MoveTo(RolloutStatus next, ActorContext actor, DateTime now)
    {
        var transition = Transitions.Transition(Id, Status, next, actor, now);
        if (transition is null)
        {
            return;
        }

        Record(transition);
        Status = next;
        EndedAt = next is RolloutStatus.Completed or RolloutStatus.RolledBack ? now : null;
    }
}
