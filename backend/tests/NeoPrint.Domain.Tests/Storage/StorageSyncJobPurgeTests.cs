using NeoPrint.Domain.Common;
using NeoPrint.Domain.Storage;

namespace NeoPrint.Domain.Tests.Storage;

/// <summary>
/// When the source of a migration may be emptied (plan section 2.10:
/// after <c>Storage.SourceRetentionDays</c>, with an administrator's approval).
/// </summary>
public sealed class StorageSyncJobPurgeTests
{
    private static readonly DateTime Finished = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ActorContext Admin = ActorContext.Admin(Guid.NewGuid(), "test");

    private static StorageSyncJob CompletedMigration()
    {
        var job = new StorageSyncJob { Mode = StorageSyncMode.Migration };
        job.Start(ActorContext.System(), Finished.AddHours(-1));
        job.BeginVerification(ActorContext.System(), Finished.AddMinutes(-5));
        job.Complete(ActorContext.System(), Finished);
        return job;
    }

    [Fact]
    public void The_source_may_be_emptied_once_the_retention_period_has_passed()
    {
        var job = CompletedMigration();

        job.SourcePurgeAllowedFrom(30).Should().Be(Finished.AddDays(30));

        job.RequestSourcePurge(30, Admin, Finished.AddDays(30));

        job.SourcePurgeRequestedAt.Should().Be(Finished.AddDays(30));
        job.SourcePurgeRequestedBy.Should().Be(Admin.ActorId);
        job.SourcePurgedAt.Should().BeNull("the deleting happens in the background");
    }

    [Fact]
    public void Not_a_moment_before()
    {
        var job = CompletedMigration();

        FluentActions.Invoking(() => job.RequestSourcePurge(30, Admin, Finished.AddDays(30).AddSeconds(-1)))
            .Should().Throw<DomainException>();

        job.SourcePurgeRequestedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(StorageSyncMode.Mirror)]
    public void A_mirror_never_empties_its_source(StorageSyncMode mode)
    {
        // The primary of a mirror is the live store, not a leftover.
        var job = new StorageSyncJob { Mode = mode };
        job.Start(ActorContext.System(), Finished.AddHours(-1));
        job.BeginVerification(ActorContext.System(), Finished.AddMinutes(-5));
        job.Complete(ActorContext.System(), Finished);

        job.SourcePurgeAllowedFrom(0).Should().BeNull();
        FluentActions.Invoking(() => job.RequestSourcePurge(0, Admin, Finished.AddYears(1)))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void A_migration_that_did_not_complete_keeps_its_source()
    {
        var job = new StorageSyncJob { Mode = StorageSyncMode.Migration };
        job.Start(ActorContext.System(), Finished.AddHours(-1));
        job.Fail("target unreachable", ActorContext.System(), Finished);

        job.SourcePurgeAllowedFrom(0).Should().BeNull();
        FluentActions.Invoking(() => job.RequestSourcePurge(0, Admin, Finished.AddYears(1)))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Asking_twice_is_the_same_request()
    {
        var job = CompletedMigration();
        var first = Finished.AddDays(31);

        job.RequestSourcePurge(30, Admin, first);
        job.RequestSourcePurge(30, ActorContext.Admin(Guid.NewGuid()), first.AddHours(2));

        job.SourcePurgeRequestedAt.Should().Be(first);
        job.SourcePurgeRequestedBy.Should().Be(Admin.ActorId);
    }

    [Fact]
    public void The_purge_can_only_finish_after_it_was_requested()
    {
        var job = CompletedMigration();

        FluentActions.Invoking(() => job.CompleteSourcePurge(Finished.AddDays(40)))
            .Should().Throw<DomainException>();

        job.RequestSourcePurge(30, Admin, Finished.AddDays(31));
        job.CompleteSourcePurge(Finished.AddDays(32));

        job.SourcePurgedAt.Should().Be(Finished.AddDays(32));
    }
}
