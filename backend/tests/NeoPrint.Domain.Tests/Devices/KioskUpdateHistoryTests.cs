using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Kiosks;
using NeoPrint.Domain.Releases;

namespace NeoPrint.Domain.Tests.Devices;

/// <summary>
/// A kiosk's update reports (T-326): every one goes into a history of the
/// last fifty, and a package the kiosk refused at its checks is not offered
/// to it again - like a version it took back.
/// </summary>
public sealed class KioskUpdateHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Every_report_goes_into_the_history_and_only_the_last_fifty_are_kept()
    {
        var kiosk = new Kiosk();

        for (var i = 0; i < Kiosk.UpdateHistoryLength + 5; i++)
        {
            kiosk.ReportUpdate(ReleaseComponent.App, $"1.0.{i}", KioskUpdateStatus.Ready, null, Now.AddMinutes(i)).Should().BeTrue();
        }

        kiosk.UpdateHistory.Should().HaveCount(Kiosk.UpdateHistoryLength);
        kiosk.UpdateHistory[0].Version.Should().Be("1.0.5", "the oldest go first");
        kiosk.UpdateHistory[^1].Version.Should().Be($"1.0.{Kiosk.UpdateHistoryLength + 4}");
        kiosk.UpdateStates.Should().ContainSingle("the latest report of each component is still the state");
    }

    [Fact]
    public void A_report_that_arrived_late_is_not_history()
    {
        var kiosk = new Kiosk();
        kiosk.ReportUpdate(ReleaseComponent.App, "1.0.1", KioskUpdateStatus.Applying, null, Now.AddMinutes(1));

        kiosk.ReportUpdate(ReleaseComponent.App, "1.0.1", KioskUpdateStatus.Ready, null, Now).Should().BeFalse();

        kiosk.UpdateHistory.Should().ContainSingle();
    }

    [Fact]
    public void A_package_refused_at_its_checks_is_skipped_and_an_ordinary_failure_is_not()
    {
        var kiosk = new Kiosk();

        kiosk.ReportUpdate(ReleaseComponent.App, "1.0.1", KioskUpdateStatus.Failed, "updates.downloadFailed", Now);
        kiosk.Skips(ReleaseComponent.App, "1.0.1").Should().BeFalse("a download that failed is tried again");

        kiosk.ReportUpdate(ReleaseComponent.App, "1.0.2", KioskUpdateStatus.Failed, "updates.signatureRefused", Now.AddMinutes(1), packageRejected: true);
        kiosk.Skips(ReleaseComponent.App, "1.0.2").Should().BeTrue("it would only be refused again");

        kiosk.RetryUpdates().Should().BeTrue();
        kiosk.Skips(ReleaseComponent.App, "1.0.2").Should().BeFalse("\"İndi yenilə\" tries it again");
    }

    [Fact]
    public void An_incident_starts_seen_once_and_says_whether_it_is_told()
    {
        var told = Incident.Raise(IncidentType.UpdatePackageRejected, IncidentSeverity.High, new UserMessage("x"), "k1", Now);
        var quiet = Incident.Raise(IncidentType.UpdatePackageRejected, IncidentSeverity.High, new UserMessage("x"), "k2", Now, quiet: true);

        told.Occurrences.Should().Be(1);
        told.LastSeenAt.Should().Be(Now);
        told.Quiet.Should().BeFalse();
        quiet.Quiet.Should().BeTrue();
    }
}
