using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Updates;
using NeoPrint.Kiosk.Platform.Abstractions;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The UI's side of an update (T-321, docs/KIOSK_UPDATES.md): it waits for the
/// update window and a moment with no customer, turns new customers away when
/// that takes too long, refuses what does not check out, goes on with a
/// download that broke off, reports each step and answers the agent commands.
/// </summary>
public sealed class UpdateCoordinatorTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeAppPaths _app = new();
    private readonly UpdatePaths _paths;
    private readonly Backend _backend = new();
    private readonly TestClock _clock = new(new DateTime(2026, 10, 25, 1, 58, 0, DateTimeKind.Utc));
    private readonly Screen _screen = new();
    private int _delays;

    public UpdateCoordinatorTests()
    {
        _app.EnsureCreated();
        _paths = new UpdatePaths(Path.Combine(_app.Root, "opt"), Path.Combine(_app.Root, "updates"), Path.Combine(_app.Root, "agent"));
        AgentFiles.Write(_paths.StateFile, new AgentState { AgentVersion = "1.0.0", Installed = new() { ["App"] = "0.9.0", ["Agent"] = "1.0.0" } });
    }

    public void Dispose()
    {
        _key.Dispose();
        _stranger.Dispose();
        Directory.Delete(_app.Root, recursive: true);
    }

    [Fact]
    public async Task Outside_the_update_window_it_waits_and_inside_it_hands_the_package_over_at_the_first_idle_moment()
    {
        var coordinator = await CoordinatorAsync();
        _backend.Offer(Package("1.0.0"));
        _screen.Idle = true;
        AnswerAs(AgentOutcome.Succeeded, "1.0.0");

        (await coordinator.CheckAsync(force: false, commandId: null, CancellationToken.None)).Should().BeTrue();

        _clock.UtcNow.TimeOfDay.Should().BeGreaterThanOrEqualTo(new TimeSpan(2, 0, 0), "nothing was handed over before the window opened");
        _backend.Statuses.Select(s => s.Status).Should().Equal("Downloading", "Ready", "WaitingForIdle", "Applying", "Succeeded");
        _screen.Updating.Should().Equal(true, false);
        _screen.Draining.Should().Equal(false);
        File.ReadAllBytes(Path.Combine(_paths.Downloads, $"{_backend.Offered!.ReleaseId:N}.pkg")).Should().Equal(_backend.Package);
    }

    [Fact]
    public async Task An_urgent_release_does_not_wait_for_the_window_and_turns_customers_away_when_none_leaves()
    {
        var coordinator = await CoordinatorAsync(start: new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc));
        _backend.Offer(Package("1.0.0"), urgent: true);
        _screen.Idle = false;
        _screen.IdleAfter = _clock.UtcNow.AddMinutes(45);
        AnswerAs(AgentOutcome.Succeeded, "1.0.0");

        await coordinator.CheckAsync(force: false, commandId: null, CancellationToken.None);

        _backend.Statuses.Select(s => s.Status).Should().Equal("Downloading", "Ready", "WaitingForIdle", "Draining", "Applying", "Succeeded");
        _screen.Draining.Should().Equal(true, false);
        _clock.UtcNow.Should().BeAfter(new DateTime(2026, 10, 25, 12, 30, 0, DateTimeKind.Utc), "customers were turned away only after 30 minutes");
    }

    [Fact]
    public async Task A_package_that_does_not_check_out_is_reported_and_never_handed_over()
    {
        var coordinator = await CoordinatorAsync();
        _backend.Offer(Package("1.0.0", signer: _stranger));

        await coordinator.CheckAsync(force: false, commandId: null, CancellationToken.None);

        _backend.Statuses.Select(s => (s.Status, s.MessageKey)).Should().Equal(("Downloading", null), ("Failed", "updates.signatureRefused"));
        Directory.Exists(_paths.Requests).Should().BeFalse();

        // Not taken again at the next check - only when an administrator asks.
        (await coordinator.CheckAsync(force: false, commandId: null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_download_that_broke_off_goes_on_from_where_it_stopped()
    {
        var coordinator = await CoordinatorAsync();
        _backend.Offer(Package("1.0.0", size: 300_000));
        _backend.BreakAfter = 120_000;
        _screen.Idle = true;
        AnswerAs(AgentOutcome.Succeeded, "1.0.0");

        await coordinator.CheckAsync(force: true, commandId: null, CancellationToken.None);

        _backend.Ranges.Should().Equal(null, "bytes=120000-");
        File.ReadAllBytes(Path.Combine(_paths.Downloads, $"{_backend.Offered!.ReleaseId:N}.pkg")).Should().Equal(_backend.Package);
        _backend.Statuses.Select(s => s.Status).Should().Contain("Succeeded");
    }

    [Fact]
    public async Task A_version_already_running_is_not_offered_again()
    {
        var coordinator = await CoordinatorAsync();
        _backend.Offer(Package("0.9.0"));

        (await coordinator.CheckAsync(force: false, commandId: null, CancellationToken.None)).Should().BeFalse();
        _backend.Statuses.Should().BeEmpty();
    }

    [Fact]
    public async Task The_agents_answer_after_the_UI_was_switched_is_reported_by_the_next_UI_once()
    {
        var commandId = Guid.NewGuid();
        AgentFiles.Write(Path.Combine(_paths.Results, $"{commandId:N}.json"), new AgentResult(commandId, AgentRequestKind.Install, AgentOutcome.RolledBack,
            _clock.UtcNow, commandId, "App", "1.0.0", "0.9.0", "updates.healthTimeout"));
        var coordinator = await CoordinatorAsync();

        await coordinator.ReportResultsAsync(CancellationToken.None);
        await coordinator.ReportResultsAsync(CancellationToken.None);

        _backend.Statuses.Should().ContainSingle().Which.Should().Be(("App", "1.0.0", "RolledBack", "updates.healthTimeout"));
        _backend.Answers.Should().ContainSingle().Which.Should().Be((commandId.ToString(), false, "updates.healthTimeout"));
    }

    [Fact]
    public async Task Restarting_the_agent_is_asked_at_once_and_answered_when_the_agent_says_so()
    {
        var coordinator = await CoordinatorAsync();
        var commandId = Guid.NewGuid();
        AnswerAs(AgentOutcome.Succeeded, "1.0.0");

        (await coordinator.HandleCommandAsync(commandId, "RestartAgent", acknowledged: false, CancellationToken.None)).Should().BeNull();
        await Eventually(() => _backend.Answers.Count == 1);

        _backend.Acknowledged.Should().Equal(commandId.ToString());
        _backend.Answers.Single().Should().Be((commandId.ToString(), true, "kioskCommands.agentRestarted"));
    }

    [Fact]
    public async Task Uploading_logs_sends_them_cleaned_and_answers_the_command()
    {
        File.WriteAllText(Path.Combine(_app.LogDirectory, $"kiosk-{_clock.UtcNow:yyyyMMdd}.log"),
            "{\"m\":\"Heartbeat answered\"}\n{\"m\":\"Authorization: Bearer eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln\"}\n{\"m\":\"SMS +994 50 123 45 67\"}\n");
        var coordinator = await CoordinatorAsync();
        var commandId = Guid.NewGuid();

        await coordinator.HandleCommandAsync(commandId, "UploadLogs", acknowledged: false, CancellationToken.None);
        await Eventually(() => _backend.Answers.Count == 1);

        var text = Gunzip(_backend.Logs!);
        text.Should().Contain("Heartbeat answered").And.NotContain("eyJhbGci").And.NotContain("123 45 67");
        _backend.LogCommand.Should().Be(commandId.ToString());
        _backend.Answers.Single().Should().Be((commandId.ToString(), true, "kioskCommands.logsUploaded"));
    }

    [Fact]
    public void A_healthy_UI_marks_its_own_version_for_the_agent()
    {
        var coordinator = new UpdateCoordinator(Api(), _paths, Check(), _screen, () => null, _clock, NullLogger.Instance, "1.0.0", _app.LogDirectory);

        coordinator.MarkHealthy();

        File.Exists(_paths.HealthMarker("App", "1.0.0")).Should().BeTrue();
    }

    [Theory]
    [InlineData("02:00-05:00", "01:59", false)]
    [InlineData("02:00-05:00", "02:00", true)]
    [InlineData("02:00-05:00", "04:59", true)]
    [InlineData("02:00-05:00", "05:00", false)]
    [InlineData("23:00-02:00", "23:30", true)]
    [InlineData("23:00-02:00", "01:00", true)]
    [InlineData("23:00-02:00", "12:00", false)]
    [InlineData("rubbish", "03:00", true)]
    public void The_update_window_is_read_as_the_setting_says(string window, string time, bool inside)
        => UpdateWindow.Parse(window).Contains(TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(inside);

    // ---------------------------------------------------------------------

    private async Task<UpdateCoordinator> CoordinatorAsync(DateTime? start = null)
    {
        if (start is { } at)
        {
            _clock.UtcNow = at;
        }

        var api = Api();
        await api.RegisterAsync(
            new MachineFingerprint("test-host", "Linux", "6.1", "machine-1", [], "CPU", 8, "1920x1080"), "Linux", "0.9.0", enrollmentToken: null, printers: []);

        return new UpdateCoordinator(
            api, _paths, Check(), _screen, () => null, _clock, NullLogger.Instance, "0.9.0", _app.LogDirectory,
            localTime: utc => TimeOnly.FromDateTime(utc),
            delay: (wait, cancellationToken) =>
            {
                if (++_delays > 10_000)
                {
                    throw new OperationCanceledException("The test's clock ran too long.");
                }

                _clock.UtcNow += wait;
                _screen.Tick(_clock.UtcNow);
                _agentAnswer?.Invoke();
                return Task.CompletedTask;
            });
    }

    private Action? _agentAnswer;

    /// <summary>The agent: whatever request appears is answered so.</summary>
    private void AnswerAs(AgentOutcome outcome, string version)
        => _agentAnswer = () =>
        {
            if (!Directory.Exists(_paths.Requests))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(_paths.Requests))
            {
                var request = AgentFiles.Read<AgentRequest>(file)!;
                AgentFiles.Write(Path.Combine(_paths.Results, $"{request.Id:N}.json"),
                    new AgentResult(request.Id, request.Kind, outcome, _clock.UtcNow, request.CommandId, request.Kind == AgentRequestKind.Install ? "App" : "Agent", version, "0.9.0", null));
                File.Delete(file);
            }
        };

    private KioskApiClient Api()
        => new(new HttpClient(_backend) { BaseAddress = new Uri("http://backend.test/") }, new FakeDeviceKeyStore(_app), NullLogger<KioskApiClient>.Instance);

    private ReleasePackageCheck Check() => new(new ReleaseKeySet("staging", [ReleasePublicKey.FromKey(_key)]));

    private sealed record TestPackage(byte[] Bytes, string Manifest, string Signature, string Version);

    private TestPackage Package(string version, ECDsa? signer = null, int size = 4096)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        var file = $"neoprint-kiosk-app-{version}-{UpdatePaths.Platform}-staging.tar.gz";
        var manifest = new ReleaseManifest(ReleaseComponent.App, UpdatePaths.Platform, "staging", ReleaseVersion.Parse(version), ReleaseVersion.Parse("0.1.0"),
            file, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "b6d4703", DateTimeOffset.UtcNow).ToBytes();
        return new TestPackage(bytes, Encoding.UTF8.GetString(manifest), Encoding.UTF8.GetString(ReleaseSignature.Sign(signer ?? _key, manifest).ToBytes()), version);
    }

    private static string Gunzip(byte[] bytes)
    {
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue();
    }

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; set; } = now;
    }

    /// <summary>The kiosk's screen: idle or not, and what it was told.</summary>
    private sealed class Screen : IUpdateHost
    {
        public bool Idle { get; set; }

        /// <summary>When the last customer leaves.</summary>
        public DateTime? IdleAfter { get; set; }

        public List<bool> Draining { get; } = [];

        public List<bool> Updating { get; } = [];

        public bool IsIdle => Idle;

        public void Tick(DateTime now)
        {
            if (IdleAfter is { } after && now >= after)
            {
                Idle = true;
            }
        }

        public void SetDraining(bool draining)
        {
            if (Draining.Count == 0 || Draining[^1] != draining)
            {
                Draining.Add(draining);
            }
        }

        public void SetUpdating(bool updating)
        {
            if (Updating.Count == 0 || Updating[^1] != updating)
            {
                Updating.Add(updating);
            }
        }
    }

    /// <summary>The backend: one offer, a download that can break off, and what the kiosk said.</summary>
    private sealed class Backend : HttpMessageHandler
    {
        public KioskReleaseOffer? Offered { get; private set; }

        public byte[] Package { get; private set; } = [];

        public long? BreakAfter { get; set; }

        public List<string?> Ranges { get; } = [];

        public List<(string Component, string Version, string Status, string? MessageKey)> Statuses { get; } = [];

        public List<string> Acknowledged { get; } = [];

        public List<(string Id, bool Succeeded, string? MessageKey)> Answers { get; } = [];

        public byte[]? Logs { get; private set; }

        public string? LogCommand { get; private set; }

        public void Offer(TestPackage package, bool urgent = false)
        {
            Package = package.Bytes;
            Offered = new KioskReleaseOffer("App", Guid.NewGuid(), package.Version, package.Manifest, package.Signature, package.Bytes.Length,
                "api/releases/x/download", urgent);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/register", StringComparison.Ordinal))
            {
                return Json(new { deviceId = Guid.NewGuid(), activated = true, kioskId = Guid.NewGuid(), kioskCode = "KSK-0001" });
            }

            if (path.EndsWith("/token", StringComparison.Ordinal) || path.EndsWith("/challenge", StringComparison.Ordinal))
            {
                return Json(new { accessToken = "kiosk-token", expiresAt = DateTime.UtcNow.AddHours(1), tokenType = "kiosk", nonce = "nonce" });
            }

            if (path == "/api/kiosks/me/update")
            {
                return Json(new { platform = UpdatePaths.Platform, keySet = "staging", offers = Offered is null ? [] : new[] { Offered }, withdrawn = Array.Empty<string>() });
            }

            if (path.EndsWith("/download", StringComparison.Ordinal))
            {
                Ranges.Add(request.Headers.Range?.ToString());
                var from = request.Headers.Range?.Ranges.Single().From ?? 0;
                var rest = Package[(int)from..];
                if (BreakAfter is { } cut && from < cut)
                {
                    // The line drops: some bytes arrive, then the stream fails.
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(rest[..(int)(cut - from)])) };
                }

                var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(rest) };
                if (from > 0)
                {
                    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, Package.Length - 1, Package.Length);
                }

                return response;
            }

            if (path == "/api/kiosks/me/update-status")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var root = body.RootElement;
                Statuses.Add((root.GetProperty("component").GetString()!, root.GetProperty("version").GetString()!, root.GetProperty("status").GetString()!,
                    root.TryGetProperty("messageKey", out var key) ? key.GetString() : null));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path == "/api/kiosks/me/logs")
            {
                Logs = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                LogCommand = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["commandId"];
                return Json(new { id = Guid.NewGuid() });
            }

            var parts = path.Split('/');
            if (path.EndsWith("/ack", StringComparison.Ordinal))
            {
                Acknowledged.Add(parts[^2]);
                return Json(new { });
            }

            if (path.EndsWith("/result", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Answers.Add((parts[^2], body.RootElement.GetProperty("succeeded").GetBoolean(), body.RootElement.GetProperty("messageKey").GetString()));
                return Json(new { });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Gives its bytes, then fails as a dropped connection does.</summary>
    private sealed class BreakingStream(byte[] bytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= bytes.Length)
            {
                throw new IOException("The connection was reset.");
            }

            var read = Math.Min(count, bytes.Length - _position);
            Array.Copy(bytes, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
