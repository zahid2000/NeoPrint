using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;
using NeoPrint.Kiosk.ViewModels;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk's own analytics (plan section 2.17): kept on disk until the
/// backend has them, sent in batches that keep their key across a retry, and
/// the events a customer's visit produces.
/// </summary>
[Collection(KioskLanguageCollection.Name)]
public sealed class KioskAnalyticsTests : IDisposable
{
    private readonly FakeAppPaths _paths = new();
    private readonly string _drive = Path.Combine(Path.GetTempPath(), "neoprint-drive", Guid.NewGuid().ToString("N"));
    private readonly List<OrderTestKit> _kits = [];

    public KioskAnalyticsTests() => Directory.CreateDirectory(_drive);

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }

        Directory.Delete(_drive, recursive: true);

        foreach (var kit in _kits)
        {
            kit.Dispose();
        }
    }

    private AnalyticsQueue Queue() => new(_paths, new SystemClock());

    [Fact]
    public async Task Events_wait_on_disk_and_go_in_batches()
    {
        var queue = Queue();
        queue.NewSession();

        for (var index = 0; index < AnalyticsQueue.BatchSize + 5; index++)
        {
            queue.Track("screen_view", new Dictionary<string, string?> { ["screen"] = "Browse" });
        }

        // A restart: the events are still there.
        var restarted = Queue();
        restarted.Waiting.Should().Be(AnalyticsQueue.BatchSize + 5);

        var batches = new List<(string Key, int Count)>();
        var delivered = await restarted.FlushAsync(
            (key, events, _) =>
            {
                batches.Add((key, events.Count));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        delivered.Should().Be(AnalyticsQueue.BatchSize + 5);
        batches.Select(batch => batch.Count).Should().Equal(AnalyticsQueue.BatchSize, 5);
        batches.Select(batch => batch.Key).Should().OnlyHaveUniqueItems();
        restarted.Waiting.Should().Be(0);
    }

    [Fact]
    public async Task A_batch_that_did_not_arrive_goes_again_under_the_same_key()
    {
        var queue = Queue();
        queue.Track("screen_view");
        var keys = new List<string>();

        var failing = () => queue.FlushAsync(
            (key, _, _) =>
            {
                keys.Add(key);
                throw new HttpRequestException("offline");
            },
            CancellationToken.None);

        await failing.Should().ThrowAsync<HttpRequestException>();

        // More happens while the kiosk is offline; it waits behind the batch.
        queue.Track("price_shown");

        await Queue().FlushAsync(
            (key, _, _) =>
            {
                keys.Add(key);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        keys.Should().HaveCount(3);
        keys[1].Should().Be(keys[0], "a lost answer must not store the batch twice");
        keys[2].Should().NotBe(keys[0]);
    }

    [Fact]
    public async Task A_kiosk_that_never_reached_the_backend_sends_only_the_newest()
    {
        var queue = Queue();

        for (var index = 0; index < AnalyticsQueue.MaxQueued + 10; index++)
        {
            queue.Track("screen_view", new Dictionary<string, string?> { ["screen"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        var first = (string?)null;
        var delivered = await queue.FlushAsync(
            (_, events, _) =>
            {
                first ??= events[0].Props["screen"];
                return Task.CompletedTask;
            },
            CancellationToken.None);

        delivered.Should().Be(AnalyticsQueue.MaxQueued);
        first.Should().Be("10", "the oldest ten went");
    }

    [Fact]
    public async Task A_visit_reports_the_file_the_price_and_the_order_and_nothing_personal()
    {
        var recorder = new Recorder();
        var kit = await OrderTestKit.CreateAsync();
        _kits.Add(kit);
        kit.Pdf(3, _drive, "Aliyev_passport.pdf");

        var flow = new PrintFlowViewModel(kit.Orders, new FakeRemovableDriveWatcher(), recorder, pageImage: _ => null) { Language = "az" };
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());
        flow.ContinueCommand.Execute(null);
        await flow.PlaceCommand.ExecuteAsync(null);
        flow.LeaveCommand.Execute(null);

        recorder.Names.Should().ContainInOrder("screen_view", "file_selected", "screen_view", "price_shown", "options_set", "order_placed");
        recorder.Names.Should().NotContain("flow_abandoned", "the visit ended in an order");
        recorder.Of("file_selected")["format"].Should().Be("pdf");
        recorder.Of("file_selected")["pages"].Should().Be("3");
        recorder.Of("order_placed")["jobCode"].Should().Be("482913");
        recorder.Events.SelectMany(recorded => recorded.Props.Values)
            .Should().NotContain(value => value != null && value.Contains("Aliyev", StringComparison.OrdinalIgnoreCase), "file names stay on the kiosk");
        recorder.Sessions.Should().ContainSingle("one visit, one session");
    }

    [Fact]
    public async Task Paying_is_reported_under_the_names_the_backend_keeps_with_the_method_and_how_long()
    {
        // T-214: the kiosk sent "payment_method" and "payment_ended", which the
        // backend's catalogue does not list - every one of them was dropped.
        var recorder = new Recorder();
        var kit = await OrderTestKit.CreateAsync(skipPayment: false);
        _kits.Add(kit);
        kit.Pdf(1, _drive, "a.pdf");

        var flow = new PrintFlowViewModel(kit.Orders, new FakeRemovableDriveWatcher(), recorder, pageImage: _ => null)
        {
            Language = "az",
            Delay = (_, token) => Task.Delay(5, token),
        };
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());
        flow.ContinueCommand.Execute(null);
        await flow.PlaceCommand.ExecuteAsync(null);

        await flow.PayWithBankCommand.ExecuteAsync(null);
        var declined = kit.Backend.Payments.Values.Single();
        kit.Backend.Payments[declined.Id] = declined with { Status = "Failed", QrContent = null };
        await flow.RefreshPaymentAsync();

        flow.RetryPaymentCommand.Execute(null);
        await flow.PayWithAppCommand.ExecuteAsync(null);
        var paid = kit.Backend.Payments.Values.Single(payment => payment.Id != declined.Id);
        kit.Backend.Payments[paid.Id] = paid with { Status = "Succeeded", PaidAt = DateTime.UtcNow };
        await flow.RefreshPaymentAsync();

        // Paid with the app, so the flash file joins the account in the
        // background (plan 9.2, step 5). The kit's folder is deleted only once
        // that is done - deleting it under the upload failed now and then.
        for (var i = 0; i < 400 && !(kit.Backend.Uploads.Count == 1 && kit.FlashUploads.Pending == 0); i++)
        {
            await Task.Delay(10);
        }

        kit.Backend.Uploads.Should().ContainSingle("the file reached the account");
        kit.FlashUploads.Pending.Should().Be(0);

        recorder.Names.Should().ContainInOrder("order_placed", "payment_started", "payment_failed", "payment_started", "payment_succeeded");
        recorder.Names.Should().NotContain(["payment_method", "payment_ended"]);
        recorder.Events.First(recorded => recorded.Name == "payment_started").Props["method"].Should().Be("BankQr");
        recorder.Of("payment_failed")["method"].Should().Be("BankQr");
        recorder.Of("payment_succeeded")["method"].Should().Be("MobileApp");
        recorder.Of("payment_succeeded")["seconds"].Should().MatchRegex("^[0-9]+$");
        recorder.Sessions.Should().ContainSingle("paying is part of the same visit");
    }

    [Fact]
    public async Task Walking_away_is_reported_with_the_screen_and_how_long()
    {
        var recorder = new Recorder();
        var kit = await OrderTestKit.CreateAsync();
        _kits.Add(kit);
        kit.Pdf(1, _drive, "a.pdf");

        var flow = new PrintFlowViewModel(kit.Orders, new FakeRemovableDriveWatcher(), recorder, pageImage: _ => null) { Language = "az" };
        await flow.StartFromDriveAsync(new RemovableDrive(_drive, "USB", 1, 1));
        await flow.OpenCommand.ExecuteAsync(flow.Entries.Single());

        flow.Abandon();

        recorder.Of("flow_abandoned")["lastScreen"].Should().Be(nameof(FlowStage.Preview));
        recorder.Of("flow_abandoned")["seconds"].Should().NotBeNullOrEmpty();

        // The next visit is a new session.
        flow.StartCancel();
        recorder.Sessions.Should().HaveCount(2);
    }

    /// <summary>Keeps what the flow tracks.</summary>
    private sealed class Recorder : IKioskAnalytics
    {
        private string _session = string.Empty;

        public List<(string Name, string Session, IReadOnlyDictionary<string, string?> Props)> Events { get; } = [];

        public HashSet<string> Sessions { get; } = [];

        public IEnumerable<string> Names => Events.Select(recorded => recorded.Name);

        public IReadOnlyDictionary<string, string?> Of(string name) => Events.Last(recorded => recorded.Name == name).Props;

        public string NewSession()
        {
            _session = Guid.NewGuid().ToString("N");
            Sessions.Add(_session);
            return _session;
        }

        public void Track(string name, IReadOnlyDictionary<string, string?>? props = null, string? sessionId = null)
            => Events.Add((name, sessionId ?? _session, props ?? new Dictionary<string, string?>()));
    }
}
