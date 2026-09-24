using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Devices;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// State transitions reach the database, and a stale write is refused
/// (<c>docs/STATE_MACHINES.md</c>, <c>docs/IDEMPOTENCY.md</c>).
/// </summary>
/// <remarks>
/// The domain tests cover which transitions are allowed. These cover the half
/// that only shows up against a real database: that saving an aggregate also
/// writes what it recorded, and that two writers cannot silently overwrite each
/// other.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class StateTransitionTests
{
    private readonly NeoPrintApiFixture _fixture;

    public StateTransitionTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private bool Skip => _fixture.Skipped;

    private static readonly DateTime Now = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    private static KioskDevice NewDevice() => new()
    {
        PublicKey = $"spki-{Guid.NewGuid():N}",
        PairingCodeHash = "hash",
        PairingCodeExpiresAt = Now.AddMinutes(30),
    };

    [DockerFact]
    public async Task Saving_an_aggregate_writes_the_transitions_it_recorded()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IKioskDeviceRepository>();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var device = NewDevice();
        await devices.AddAsync(device);

        var actor = ActorContext.Admin(Guid.NewGuid(), "Activated in a test", "corr-persist");
        device.Activate(Guid.NewGuid(), actor, Now);

        await devices.UpdateAsync(device);

        var stored = await context.StateTransitions
            .Find(transition => transition.EntityId == device.Id)
            .ToListAsync();

        // Written by the repository, not by the handler: recording a transition
        // is not something a call site can forget.
        stored.Should().ContainSingle();
        stored[0].EntityType.Should().Be("KioskDevice");
        stored[0].FromStatus.Should().Be("Pending");
        stored[0].ToStatus.Should().Be("Active");
        stored[0].ActorType.Should().Be(ActorType.Admin);
        stored[0].Reason.Should().Be("Activated in a test");
        stored[0].CorrelationId.Should().Be("corr-persist");
    }

    [DockerFact]
    public async Task Saving_twice_does_not_write_the_same_transition_twice()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IKioskDeviceRepository>();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var device = NewDevice();
        await devices.AddAsync(device);

        device.Activate(Guid.NewGuid(), ActorContext.System(), Now);

        await devices.UpdateAsync(device);
        await devices.UpdateAsync(device);

        var stored = await context.StateTransitions
            .Find(transition => transition.EntityId == device.Id)
            .ToListAsync();

        stored.Should().ContainSingle("the pending transitions are drained as they are written");
    }

    [DockerFact]
    public async Task A_second_writer_working_from_a_stale_copy_is_refused()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IKioskDeviceRepository>();

        var device = NewDevice();
        await devices.AddAsync(device);

        // Two requests read the same document.
        var first = await devices.FindByIdAsync(device.Id);
        var second = await devices.FindByIdAsync(device.Id);

        first!.Activate(Guid.NewGuid(), ActorContext.System(), Now);
        await devices.UpdateAsync(first);

        // The second one is holding a copy from before that write. Letting it
        // save would silently discard the activation.
        second!.AgentVersion = "0.9.9";

        var act = () => devices.UpdateAsync(second);

        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    [DockerFact]
    public async Task A_refused_write_leaves_the_row_version_alone_so_a_retry_is_possible()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IKioskDeviceRepository>();

        var device = NewDevice();
        await devices.AddAsync(device);

        var stale = await devices.FindByIdAsync(device.Id);
        var staleVersion = stale!.RowVersion;

        var winner = await devices.FindByIdAsync(device.Id);
        winner!.AgentVersion = "1.0.0";
        await devices.UpdateAsync(winner);

        stale.AgentVersion = "0.9.9";
        await Assert.ThrowsAsync<ConcurrencyException>(() => devices.UpdateAsync(stale));

        // The entity is back the way it was, so a caller that reloads and
        // retries is not working from a version that was never written.
        stale.RowVersion.Should().Be(staleVersion);
    }

    [DockerFact]
    public async Task Each_save_moves_the_row_version_on()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IKioskDeviceRepository>();

        var device = NewDevice();
        await devices.AddAsync(device);

        device.AgentVersion = "1.0.0";
        await devices.UpdateAsync(device);

        device.AgentVersion = "1.0.1";
        await devices.UpdateAsync(device);

        var reloaded = await devices.FindByIdAsync(device.Id);

        reloaded!.RowVersion.Should().Be(2);
        reloaded.AgentVersion.Should().Be("1.0.1");
    }

    [DockerFact]
    public async Task Activating_a_device_through_the_api_leaves_a_trail_for_both_aggregates()
    {
        if (Skip) { return; }

        using var scope = _fixture.Services.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IKioskDeviceRepository>();
        var kiosks = scope.ServiceProvider.GetRequiredService<IKioskRepository>();
        var context = scope.ServiceProvider.GetRequiredService<MongoContext>();

        var kiosk = new Kiosk
        {
            Code = $"KSK-T{Random.Shared.Next(1000, 9999)}",
            Name = "Transition test",
        };

        await kiosks.AddAsync(kiosk);

        var device = NewDevice();
        await devices.AddAsync(device);

        var actor = ActorContext.Admin(Guid.NewGuid(), correlationId: "corr-both");

        device.Activate(kiosk.Id, actor, Now);
        kiosk.BindDevice(device.Id, KioskPlatform.Linux, "0.1.0", actor, Now);

        await devices.UpdateAsync(device);
        await kiosks.UpdateAsync(kiosk);

        var trail = await context.StateTransitions
            .Find(transition => transition.CorrelationId == "corr-both")
            .ToListAsync();

        // One request, two aggregates, one correlation id: that is what makes
        // an incident traceable (docs/OBSERVABILITY.md).
        trail.Should().HaveCount(2);
        trail.Select(transition => transition.EntityType)
            .Should().BeEquivalentTo(["KioskDevice", "Kiosk"]);
    }
}
