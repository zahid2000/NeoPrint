using NeoPrint.Domain.Common;
using NeoPrint.Domain.Files;
using NeoPrint.Domain.Privacy;

namespace NeoPrint.Domain.Tests.Privacy;

/// <summary>A "Məlumatlarımı yüklə" archive (plan section 2.13; docs/DECISIONS.md T-147).</summary>
public sealed class DataExportTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc);
    private static readonly FileObject Zip = new(Guid.NewGuid(), "exports/u/e.zip", "sha", 120, 100, "k1");

    [Fact]
    public void It_is_current_while_made_and_for_24_hours_after()
    {
        var export = DataExport.Request(Guid.NewGuid(), Now);
        export.IsCurrent(Now).Should().BeTrue();

        export.MarkReady(Zip, 100, includesFiles: true, Now.AddMinutes(1));
        export.ExpiresAt.Should().Be(Now.AddMinutes(1).AddHours(24));
        export.IsCurrent(Now.AddHours(24)).Should().BeTrue();
        export.IsCurrent(Now.AddMinutes(1).AddHours(24)).Should().BeFalse();

        export.Expire(Now.AddDays(2));
        export.Archive.Should().BeNull();
        export.Status.Should().Be(DataExportStatus.Expired);
    }

    [Fact]
    public void A_failed_one_says_why_and_is_not_current()
    {
        var export = DataExport.Request(Guid.NewGuid(), Now);
        export.MarkFailed(UserMessage.Of("privacy.exportFailed"), Now);

        export.IsCurrent(Now).Should().BeFalse();
        UserMessage.Decode(export.FailureReason!).Key.Should().Be("privacy.exportFailed");

        var ready = () => export.MarkReady(Zip, 1, true, Now);
        ready.Should().Throw<IllegalStateTransitionException>();
    }
}
