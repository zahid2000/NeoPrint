using NeoPrint.Kiosk.Printing;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The flash file of an order paid with the app (plan section 2.2): sent to
/// the account, retried while the network is down, and gone from the kiosk
/// after 24 hours of trying (docs/TASKS.md T-117).
/// </summary>
public sealed class FlashUploadsTests : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), "neoprint-upload-" + Guid.NewGuid().ToString("N") + ".pdf");
    private readonly List<OrderTestKit> _kits = [];

    public FlashUploadsTests() => File.WriteAllText(_source, "%PDF-1.4 test");

    public void Dispose()
    {
        foreach (var kit in _kits)
        {
            kit.Dispose();
        }

        File.Delete(_source);
    }

    private async Task<OrderTestKit> KitAsync()
    {
        var kit = await OrderTestKit.CreateAsync();
        _kits.Add(kit);
        return kit;
    }

    [Fact]
    public async Task A_file_that_cannot_be_sent_is_kept_and_sent_when_the_network_is_back()
    {
        var kit = await KitAsync();
        var uploads = kit.FlashUploads;
        var jobId = Guid.NewGuid();
        kit.Backend.UploadsDown = true;

        await uploads.KeepAndSendAsync(jobId, _source, "hesabat.pdf", CancellationToken.None);

        uploads.Pending.Should().Be(1);
        kit.Backend.Uploads.Should().BeEmpty();

        kit.Backend.UploadsDown = false;
        (await uploads.SendPendingAsync(CancellationToken.None)).Should().Be(0);

        kit.Backend.Uploads.Should().ContainSingle().Which.Should().Be((jobId, "hesabat.pdf"));
        kit.Spool.Exists($"upload-{jobId:N}").Should().BeFalse("a sent file leaves the kiosk");
    }

    [Fact]
    public async Task After_24_hours_of_trying_the_file_leaves_the_kiosk_unsent()
    {
        var kit = await KitAsync();
        var uploads = kit.FlashUploads;
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        uploads.Now = () => now;
        kit.Backend.UploadsDown = true;

        await uploads.KeepAndSendAsync(jobId, _source, "hesabat.pdf", CancellationToken.None);

        now += FlashUploads.Lifetime - TimeSpan.FromMinutes(1);
        (await uploads.SendPendingAsync(CancellationToken.None)).Should().Be(1, "inside 24 hours it is still tried");
        kit.Spool.Exists($"upload-{jobId:N}").Should().BeTrue();

        now += TimeSpan.FromMinutes(2);
        kit.Backend.UploadsDown = false;
        (await uploads.SendPendingAsync(CancellationToken.None)).Should().Be(0);

        kit.Backend.Uploads.Should().BeEmpty("past 24 hours it is not sent any more");
        kit.Spool.Exists($"upload-{jobId:N}").Should().BeFalse("a customer's file is not kept on the kiosk past the plan's 24 hours");
    }
}
