using NeoPrint.Domain.Common;

namespace NeoPrint.Domain.Tests.Common;

/// <summary>
/// The mechanism every aggregate's transitions are declared with
/// (<c>docs/STATE_MACHINES.md</c>).
/// </summary>
public sealed class StateMachineTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    private static readonly ActorContext Actor =
        ActorContext.Admin(Guid.NewGuid(), "because", "corr-1");

    private enum Light
    {
        Red = 0,
        Green = 1,
        Amber = 2,
        BurntOut = 3,
    }

    private static StateMachine<Light> Traffic() =>
        new StateMachine<Light>("Light")
            .Allow(Light.Red, Light.Green)
            .Allow(Light.Green, Light.Amber)
            .Allow(Light.Amber, Light.Red);

    [Fact]
    public void A_declared_transition_is_allowed()
    {
        Traffic().IsAllowed(Light.Red, Light.Green).Should().BeTrue();
    }

    [Fact]
    public void Anything_not_declared_is_refused()
    {
        var machine = Traffic();

        // Fails closed: a status added later is refused everywhere until
        // somebody declares where it may go.
        machine.IsAllowed(Light.Red, Light.Amber).Should().BeFalse();
        machine.IsAllowed(Light.Green, Light.Red).Should().BeFalse();
        machine.IsAllowed(Light.BurntOut, Light.Green).Should().BeFalse();
    }

    [Fact]
    public void An_illegal_transition_throws_and_says_what_it_was()
    {
        var act = () => Traffic().Transition(Guid.NewGuid(), Light.Red, Light.Amber, Actor, Now);

        act.Should().Throw<IllegalStateTransitionException>()
            .WithMessage("*Red*Amber*")
            .Which.EntityType.Should().Be("Light");
    }

    [Fact]
    public void An_illegal_transition_is_a_domain_exception()
    {
        // So it surfaces as a 409 with a readable message rather than a 500.
        var act = () => Traffic().Transition(Guid.NewGuid(), Light.Red, Light.Amber, Actor, Now);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Moving_to_the_status_it_is_already_in_records_nothing()
    {
        // The same event arriving twice must not produce a second record or an
        // error (docs/STATE_MACHINES.md: transitions are idempotent).
        Traffic().Transition(Guid.NewGuid(), Light.Red, Light.Red, Actor, Now).Should().BeNull();
    }

    [Fact]
    public void A_transition_carries_the_actor_and_the_correlation_id()
    {
        var entityId = Guid.NewGuid();

        var transition = Traffic().Transition(entityId, Light.Red, Light.Green, Actor, Now);

        transition.Should().NotBeNull();
        transition!.EntityType.Should().Be("Light");
        transition.EntityId.Should().Be(entityId);
        transition.FromStatus.Should().Be("Red");
        transition.ToStatus.Should().Be("Green");
        transition.ActorType.Should().Be(ActorType.Admin);
        transition.ActorId.Should().Be(Actor.ActorId);
        transition.Reason.Should().Be("because");
        transition.CorrelationId.Should().Be("corr-1");
        transition.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void Statuses_are_recorded_by_name_not_by_number()
    {
        var transition = Traffic().Transition(Guid.NewGuid(), Light.Red, Light.Green, Actor, Now);

        // A number would start meaning something else the day a member is
        // inserted into the enum.
        transition!.FromStatus.Should().Be("Red");
        transition.ToStatus.Should().Be("Green");
    }

    [Fact]
    public void A_sink_can_be_reached_from_every_status_but_itself()
    {
        var machine = Traffic().AllowFromAny(Light.BurntOut);

        machine.IsAllowed(Light.Red, Light.BurntOut).Should().BeTrue();
        machine.IsAllowed(Light.Green, Light.BurntOut).Should().BeTrue();
        machine.IsAllowed(Light.Amber, Light.BurntOut).Should().BeTrue();

        // Not a self-transition: those are no-ops, handled above.
        machine.IsAllowed(Light.BurntOut, Light.BurntOut).Should().BeFalse();

        // And it does not open anything else.
        machine.IsAllowed(Light.BurntOut, Light.Green).Should().BeFalse();
    }

    [Fact]
    public void Declaring_the_same_transition_twice_is_harmless()
    {
        var machine = new StateMachine<Light>("Light")
            .Allow(Light.Red, Light.Green)
            .Allow(Light.Red, Light.Green, Light.Amber);

        machine.IsAllowed(Light.Red, Light.Green).Should().BeTrue();
        machine.IsAllowed(Light.Red, Light.Amber).Should().BeTrue();
    }
}
