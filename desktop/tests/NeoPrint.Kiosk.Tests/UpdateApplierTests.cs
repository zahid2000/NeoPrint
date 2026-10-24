using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Crypto.Releases;
using NeoPrint.Kiosk.Agent.Updates;
using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Core.Updates;

using Xunit;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The agent's side of an update (T-321, docs/KIOSK_UPDATES.md): it trusts the
/// UI with nothing, checks its own copy, switches atomically, rolls back a
/// version that fails its health check, and after a power cut finishes or
/// undoes what was half done.
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private const string Running = "0.9.0";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _root = Directory.CreateTempSubdirectory("neoprint-agent-").FullName;
    private readonly UpdatePaths _paths;
    private readonly TestClock _clock = new();
    private readonly FakeUi _ui = new();
    private readonly FakeProvisioning _provisioning = new();
    private int _restarts;

    public UpdateApplierTests()
    {
        _paths = new UpdatePaths(Path.Combine(_root, "opt"), Path.Combine(_root, "updates"), Path.Combine(_root, "agent"));
        _ui.Paths = _paths;

        // A kiosk on 0.9.0: what provisioning left behind.
        var running = Path.Combine(_paths.Versions(UpdateComponents.App), Running);
        Directory.CreateDirectory(running);
        File.WriteAllText(Path.Combine(running, "NeoPrint.Kiosk"), "old");
        File.WriteAllText(Path.Combine(running, VersionLinks.CompleteMarker), Running);
        new VersionLinks(_paths.Versions(UpdateComponents.App), _paths.Current(UpdateComponents.App)).SwitchTo(Running);
    }

    public void Dispose()
    {
        _key.Dispose();
        _stranger.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_signed_package_is_installed_and_the_previous_version_kept()
    {
        var agent = await StartedAsync();
        _ui.HealthyOnStart = true;

        var result = await agent.ProcessAsync(Request(Package("1.0.0")), CancellationToken.None);

        result!.Outcome.Should().Be(AgentOutcome.Succeeded);
        result.Version.Should().Be("1.0.0");
        result.From.Should().Be(Running);
        Links().CurrentVersion().Should().Be("1.0.0");
        File.ReadAllText(Path.Combine(_paths.Current(UpdateComponents.App), "NeoPrint.Kiosk")).Should().Be("new 1.0.0");
        agent.State.Installed[UpdateComponents.App].Should().Be("1.0.0");
        agent.State.Previous[UpdateComponents.App].Should().Be(Running);
        Directory.Exists(Links().VersionFolder(Running)).Should().BeTrue("the previous version stays for a rollback");
        File.Exists(_paths.JournalFile).Should().BeFalse();
        _ui.Calls.Should().Equal("stop", "start");
        Directory.GetFiles(_paths.Incoming).Should().BeEmpty("the agent's copy is not left behind");
    }

    [Fact]
    public async Task A_version_that_does_not_pass_its_health_check_is_rolled_back_and_not_taken_again()
    {
        var agent = await StartedAsync();
        _ui.HealthyOnStart = false;
        var package = Package("1.0.0");

        var result = await agent.ProcessAsync(Request(package), CancellationToken.None);

        result!.Outcome.Should().Be(AgentOutcome.RolledBack);
        result.MessageKey.Should().Be("updates.healthTimeout");
        Links().CurrentVersion().Should().Be(Running);
        agent.State.Installed[UpdateComponents.App].Should().Be(Running);
        _ui.Calls.Should().Equal("stop", "start", "stop", "start");

        (await agent.ProcessAsync(Request(package), CancellationToken.None))!.MessageKey.Should().Be("updates.refused.failedHere");

        // "İndi yenilə": tried again on purpose.
        _ui.HealthyOnStart = true;
        (await agent.ProcessAsync(Request(package) with { Retry = true }, CancellationToken.None))!.Outcome.Should().Be(AgentOutcome.Succeeded);
    }

    [Theory]
    [InlineData("stranger", "updates.signatureRefused")]
    [InlineData("changed", "updates.packageMismatch")]
    [InlineData("production", "updates.refused.wrongTarget")]
    [InlineData("older", "updates.notNewer")]
    [InlineData("same", "updates.notNewer")]
    [InlineData("withdrawn", "updates.withdrawn")]
    public async Task What_the_UI_hands_over_is_refused_unless_it_checks_out(string kind, string reason)
    {
        var agent = await StartedAsync();
        _ui.HealthyOnStart = true;

        var package = kind switch
        {
            "stranger" => Package("1.0.0", signer: _stranger),
            "production" => Package("1.0.0", keySet: "production"),
            "older" => Package("0.8.0"),
            "same" => Package(Running),
            _ => Package("1.0.0"),
        };
        if (kind == "changed")
        {
            File.AppendAllText(Path.Combine(_paths.Downloads, package.File), "!");
        }

        var request = Request(package) with { Withdrawn = kind == "withdrawn" ? [package.Sha256] : [] };
        var result = await agent.ProcessAsync(request, CancellationToken.None);

        result!.Outcome.Should().Be(AgentOutcome.Refused);
        result.MessageKey.Should().Be(reason);
        Links().CurrentVersion().Should().Be(Running);
        _ui.Calls.Should().BeEmpty("nothing was stopped for a package that is not taken");
        Directory.Exists(Links().VersionFolder(package.Version)).Should().Be(kind is "older" or "same" && Directory.Exists(Links().VersionFolder(package.Version)));
    }

    [Fact]
    public async Task A_withdrawn_package_stays_withdrawn_though_the_UI_stops_saying_so()
    {
        var agent = await StartedAsync();
        var first = Package("1.0.0");
        await agent.ProcessAsync(Request(first) with { Withdrawn = [first.Sha256] }, CancellationToken.None);

        (await agent.ProcessAsync(Request(first) with { Withdrawn = [] }, CancellationToken.None))!.MessageKey.Should().Be("updates.withdrawn");
        agent.State.Withdrawn.Should().Contain(first.Sha256);
    }

    [Theory]
    [InlineData("../evil.pkg")]
    [InlineData("/etc/passwd")]
    [InlineData("sub/dir.pkg")]
    public async Task A_request_naming_anything_but_a_file_in_downloads_is_refused(string fileName)
    {
        var agent = await StartedAsync();
        var package = Package("1.0.0");

        var result = await agent.ProcessAsync(Request(package) with { PackageFile = fileName }, CancellationToken.None);

        result!.Outcome.Should().Be(AgentOutcome.Refused);
        _ui.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task What_is_installed_is_the_copy_the_agent_checked_not_what_the_UI_puts_there_afterwards()
    {
        _ui.HealthyOnStart = true;
        var package = Package("1.0.0");
        var agent = await StartedAsync(afterCopy: source =>
        {
            // The UI's folder is changed between the copy and the check.
            File.WriteAllBytes(source, Archive(("NeoPrint.Kiosk", "tampered")));
        });

        var result = await agent.ProcessAsync(Request(package), CancellationToken.None);

        result!.Outcome.Should().Be(AgentOutcome.Succeeded);
        File.ReadAllText(Path.Combine(Links().VersionFolder("1.0.0"), "NeoPrint.Kiosk")).Should().Be("new 1.0.0");
    }

    [Fact]
    public async Task An_archive_with_a_path_outside_its_folder_is_not_unpacked()
    {
        var agent = await StartedAsync();
        var package = Package("1.0.0", files: [("../escaped", "x")]);

        var result = await agent.ProcessAsync(Request(package), CancellationToken.None);

        result!.Outcome.Should().Be(AgentOutcome.Failed);
        result.MessageKey.Should().Be("updates.badArchive");
        File.Exists(Path.Combine(_paths.Versions(UpdateComponents.App), "escaped")).Should().BeFalse();
        Directory.GetDirectories(_paths.Versions(UpdateComponents.App)).Select(Path.GetFileName).Should().Equal(Running);
    }

    [Fact]
    public async Task After_a_power_cut_in_the_switch_the_old_version_runs_on()
    {
        WriteJournal(JournalPhase.Switching, to: "1.0.0", unpack: true);

        var agent = await StartedAsync();

        Links().CurrentVersion().Should().Be(Running);
        Result().Outcome.Should().Be(AgentOutcome.Failed);
        Result().MessageKey.Should().Be("updates.interrupted");
        File.Exists(_paths.JournalFile).Should().BeFalse();
        agent.State.Installed[UpdateComponents.App].Should().Be(Running);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task After_a_power_cut_in_the_health_check_it_is_finished_or_undone(bool healthy)
    {
        WriteJournal(JournalPhase.HealthCheck, to: "1.0.0", unpack: true, switchNow: true);
        _ui.HealthyOnStart = healthy;

        var agent = await StartedAsync();

        Result().Outcome.Should().Be(healthy ? AgentOutcome.Succeeded : AgentOutcome.RolledBack);
        Links().CurrentVersion().Should().Be(healthy ? "1.0.0" : Running);
        agent.State.Installed[UpdateComponents.App].Should().Be(healthy ? "1.0.0" : Running);
        Directory.Exists(Links().VersionFolder(Running)).Should().BeTrue();
    }

    [Fact]
    public async Task An_administrators_rollback_goes_to_the_version_kept_and_says_whose_it_was()
    {
        var agent = await StartedAsync();
        _ui.HealthyOnStart = true;
        await agent.ProcessAsync(Request(Package("1.0.0")), CancellationToken.None);
        _ui.Calls.Clear();

        var result = await agent.ProcessAsync(
            new AgentRequest(Guid.NewGuid(), AgentRequestKind.Rollback, _clock.UtcNow, CommandId: Guid.NewGuid(), HealthTimeoutSeconds: 60),
            CancellationToken.None);

        result!.Outcome.Should().Be(AgentOutcome.RolledBack);
        result.MessageKey.Should().Be("updates.rolledBackByAdmin");
        result.Version.Should().Be("1.0.0", "the version left behind is the one taken back");
        Links().CurrentVersion().Should().Be(Running);
        _ui.Calls.Should().Equal("stop", "start");
    }

    [Fact]
    public async Task Old_versions_beyond_the_kept_number_are_deleted_but_never_the_current_or_previous()
    {
        var agent = await StartedAsync();
        _ui.HealthyOnStart = true;

        foreach (var version in new[] { "1.0.0", "1.1.0", "1.2.0", "1.3.0" })
        {
            (await agent.ProcessAsync(Request(Package(version)) with { KeepVersions = 2 }, CancellationToken.None))!.Outcome.Should().Be(AgentOutcome.Succeeded);
        }

        Directory.GetDirectories(_paths.Versions(UpdateComponents.App)).Select(Path.GetFileName).Should().BeEquivalentTo(["1.3.0", "1.2.0"]);
    }

    [Fact]
    public async Task A_new_agent_switches_itself_and_its_successor_confirms_it_or_the_guard_puts_the_old_one_back()
    {
        var agent = await StartedAsync(agentVersion: Running);
        var package = Package("1.0.0", component: ReleaseComponent.Agent);

        (await agent.ProcessAsync(Request(package), CancellationToken.None)).Should().BeNull("the next agent answers");
        _restarts.Should().Be(1);
        new VersionLinks(_paths.Versions(UpdateComponents.Agent), _paths.Current(UpdateComponents.Agent)).CurrentVersion().Should().Be("1.0.0");
        File.ReadAllText(_paths.AgentPendingFile).Should().Be($"{Running} 1.0.0 0\n");

        // The new agent starts and runs: it confirms.
        var successor = await StartedAsync(agentVersion: "1.0.0");
        Result().Outcome.Should().Be(AgentOutcome.Succeeded);
        successor.State.Installed[UpdateComponents.Agent].Should().Be("1.0.0");
        File.Exists(_paths.AgentPendingFile).Should().BeFalse();

        // Another switch whose new agent never started: the guard brought the old one back.
        File.Delete(Path.Combine(_paths.Results, $"{Result().Id:N}.json"));
        AgentFiles.Write(_paths.JournalFile, new AgentJournal(Guid.NewGuid(), AgentRequestKind.Install, UpdateComponents.Agent, "1.0.0", "1.1.0",
            JournalPhase.HealthCheck, _clock.UtcNow, _clock.UtcNow.AddSeconds(30)));
        File.WriteAllText(_paths.AgentPendingFile, "1.0.0 1.1.0 3\n");

        var restored = await StartedAsync(agentVersion: "1.0.0");
        Result().Outcome.Should().Be(AgentOutcome.RolledBack);
        Result().MessageKey.Should().Be("updates.agentStartFailed");
        restored.State.FailedHere.Should().Contain("Agent@1.1.0");
    }

    [Fact]
    public async Task Provisioning_is_switched_and_run_in_repair_mode_and_taken_back_when_it_fails()
    {
        var agent = await StartedAsync();
        _provisioning.ExitCode = 0;
        (await agent.ProcessAsync(Request(Package("1.0.0", component: ReleaseComponent.Provisioning)), CancellationToken.None))!
            .Outcome.Should().Be(AgentOutcome.Succeeded);
        _provisioning.Folders.Should().ContainSingle().Which.Should().EndWith("1.0.0");

        _provisioning.ExitCode = 3;
        var failed = await agent.ProcessAsync(Request(Package("1.1.0", component: ReleaseComponent.Provisioning)), CancellationToken.None);
        failed!.Outcome.Should().Be(AgentOutcome.Failed);
        failed.MessageKey.Should().Be("updates.provisioningFailed");
        new VersionLinks(_paths.Versions(UpdateComponents.Provisioning), _paths.Current(UpdateComponents.Provisioning)).CurrentVersion().Should().Be("1.0.0");
        _ui.Calls.Should().BeEmpty("provisioning does not stop the UI");
    }

    [Fact]
    public async Task Where_the_agent_cannot_update_itself_yet_it_says_so()
    {
        var agent = await StartedAsync(platformUpdatesItself: false);

        var result = await agent.ProcessAsync(Request(Package("1.0.0", component: ReleaseComponent.Agent)), CancellationToken.None);

        result!.Outcome.Should().Be(AgentOutcome.Refused);
        result.MessageKey.Should().Be("updates.notSupportedHere");
        _restarts.Should().Be(0);
    }

    [Fact]
    public async Task A_request_is_answered_once_and_cleared_away()
    {
        var agent = await StartedAsync();
        _ui.HealthyOnStart = true;
        var request = Request(Package("1.0.0"));
        Directory.CreateDirectory(_paths.Requests);
        AgentFiles.Write(Path.Combine(_paths.Requests, $"{request.Id:N}.json"), request);

        (await agent.ProcessPendingAsync(CancellationToken.None)).Should().Be(1);
        Directory.GetFiles(_paths.Requests).Should().BeEmpty();

        // The same request written again (the UI did not see the answer): answered already, not done twice.
        AgentFiles.Write(Path.Combine(_paths.Requests, $"{request.Id:N}.json"), request);
        (await agent.ProcessPendingAsync(CancellationToken.None)).Should().Be(0);
        _ui.Calls.Should().Equal("stop", "start");
    }

    // ---------------------------------------------------------------------

    private sealed record TestPackage(string File, string Version, string Manifest, string Signature, string Sha256);

    private VersionLinks Links() => new(_paths.Versions(UpdateComponents.App), _paths.Current(UpdateComponents.App));

    private async Task<UpdateApplier> StartedAsync(string agentVersion = "1.0.0", Action<string>? afterCopy = null, bool platformUpdatesItself = true)
    {
        var agent = new UpdateApplier(
            _paths,
            new ReleasePackageCheck(new ReleaseKeySet("staging", [ReleasePublicKey.FromKey(_key)])),
            _ui,
            _provisioning,
            _clock,
            NullLogger<UpdateApplier>.Instance,
            agentVersion,
            () => _restarts++,
            (wait, _) =>
            {
                _clock.Advance(wait);
                return Task.CompletedTask;
            },
            platformUpdatesItself,
            "linux-x64")
        {
            AfterCopy = afterCopy,
        };
        await agent.StartAsync(CancellationToken.None);
        return agent;
    }

    private AgentRequest Request(TestPackage package) => new(
        Guid.NewGuid(), AgentRequestKind.Install, _clock.UtcNow, ReleaseId: Guid.NewGuid(), PackageFile: package.File,
        Manifest: package.Manifest, Signature: package.Signature, Withdrawn: [], HealthTimeoutSeconds: 60, KeepVersions: 3);

    private TestPackage Package(
        string version,
        ReleaseComponent component = ReleaseComponent.App,
        string keySet = "staging",
        ECDsa? signer = null,
        (string Name, string Text)[]? files = null)
    {
        var bytes = Archive(files ?? [("NeoPrint.Kiosk", $"new {version}"), ("lib/NeoPrint.Kiosk.dll", version)]);
        var name = $"neoprint-kiosk-{component.ToString().ToLowerInvariant()}-{version}-linux-x64-{keySet}.tar.gz";
        Directory.CreateDirectory(_paths.Downloads);
        File.WriteAllBytes(Path.Combine(_paths.Downloads, name), bytes);

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var manifest = new ReleaseManifest(component, "linux-x64", keySet, ReleaseVersion.Parse(version), ReleaseVersion.Parse("0.1.0"),
            name, bytes.Length, sha, "b6d4703", DateTimeOffset.UtcNow).ToBytes();
        var signature = ReleaseSignature.Sign(signer ?? _key, manifest).ToBytes();
        return new TestPackage(name, version, Encoding.UTF8.GetString(manifest), Encoding.UTF8.GetString(signature), sha);
    }

    private static byte[] Archive(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, text) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "./" + name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                };
                tar.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    private void WriteJournal(JournalPhase phase, string to, bool unpack, bool switchNow = false)
    {
        if (unpack)
        {
            var folder = Links().VersionFolder(to);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, VersionLinks.CompleteMarker), to);
        }

        if (switchNow)
        {
            Links().SwitchTo(to);
        }

        AgentFiles.Write(_paths.JournalFile, new AgentJournal(Guid.NewGuid(), AgentRequestKind.Install, UpdateComponents.App, Running, to,
            phase, _clock.UtcNow, _clock.UtcNow.AddMinutes(5)));
    }

    private AgentResult Result()
        => Directory.GetFiles(_paths.Results, "*.json").Select(AgentFiles.Read<AgentResult>).OfType<AgentResult>().OrderBy(r => r.At).Last();

    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; private set; } = new(2026, 10, 25, 2, 0, 0, DateTimeKind.Utc);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    /// <summary>The UI as the agent sees it: stopped, started - and, when told, healthy once started.</summary>
    private sealed class FakeUi : IUiHost
    {
        public UpdatePaths? Paths { get; set; }

        public bool HealthyOnStart { get; set; }

        public List<string> Calls { get; } = [];

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Calls.Add("start");
            if (HealthyOnStart && Paths is { } paths
                && new VersionLinks(paths.Versions(UpdateComponents.App), paths.Current(UpdateComponents.App)).CurrentVersion() is { } version)
            {
                Directory.CreateDirectory(paths.Health);
                File.WriteAllText(paths.HealthMarker(UpdateComponents.App, version), "ok");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeProvisioning : IProvisioningRunner
    {
        public int ExitCode { get; set; }

        public List<string> Folders { get; } = [];

        public Task<int> RunAsync(string scriptsFolder, CancellationToken cancellationToken)
        {
            Folders.Add(scriptsFolder);
            return Task.FromResult(ExitCode);
        }
    }
}
