using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NeoPrint.Kiosk.Core;
using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// Device enrollment on the kiosk side (plan sections 7.11, 11).
/// </summary>
/// <remarks>
/// Driven against a scripted transport, so the flow is exercised without a
/// backend. Plan section 11 rules out a mocking library, so the transport is
/// written by hand.
/// </remarks>
public sealed class DeviceEnrollmentTests : IDisposable
{
    private readonly FakeAppPaths _paths = new();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Scripted transport
    // -----------------------------------------------------------------------

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _responses = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public ScriptedHandler Respond(string path, HttpStatusCode status, string json)
        {
            if (!_responses.TryGetValue(path, out var queue))
            {
                queue = new Queue<Func<HttpResponseMessage>>();
                _responses[path] = queue;
            }

            queue.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

            return this;
        }

        public ScriptedHandler RespondStatus(string path, HttpStatusCode status)
            => Respond(path, status, "{}");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");

            if (_responses.TryGetValue(path, out var queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue()());
            }

            throw new InvalidOperationException($"No scripted response for {request.Method} {path}.");
        }
    }

    private (DeviceEnrollmentService Service, ScriptedHandler Handler, FakeDeviceKeyStore Keys) Build(
        FakePrinterService? printers = null)
    {
        var handler = new ScriptedHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://backend.test/") };
        var keys = new FakeDeviceKeyStore(_paths);

        var api = new KioskApiClient(http, keys, NullLogger<KioskApiClient>.Instance);

        var service = new DeviceEnrollmentService(
            api,
            keys,
            _paths,
            new FakeMachineFingerprintProvider(),
            printers ?? new FakePrinterService(),
            Options.Create(new KioskSettings { ApiBaseUrl = "http://backend.test" }),
            NullLogger<DeviceEnrollmentService>.Instance);

        return (service, handler, keys);
    }

    private static string RegistrationJson(Guid deviceId, bool activated, string? pairingCode) =>
        JsonSerializer.Serialize(new
        {
            deviceId,
            activated,
            pairingCode,
            pairingCodeExpiresAt = activated ? (DateTime?)null : DateTime.UtcNow.AddMinutes(30),
            kioskId = activated ? Guid.NewGuid() : (Guid?)null,
            kioskCode = activated ? "KSK-0042" : null,
        });

    /// <summary>What <c>GET /api/devices/me</c> answers while the kiosk waits.</summary>
    private static string StateJson(Guid deviceId, string status, string? kioskCode = null) =>
        JsonSerializer.Serialize(new
        {
            deviceId,
            status,
            kioskId = kioskCode is null ? (Guid?)null : Guid.NewGuid(),
            kioskCode,
            hasValidPairingCode = kioskCode is null,
            pairingCodeExpiresAt = kioskCode is null ? DateTime.UtcNow.AddMinutes(25) : (DateTime?)null,
        });

    private static string TokenJson(string tokenType, string? kioskCode = null) =>
        JsonSerializer.Serialize(new
        {
            accessToken = "token",
            expiresAt = DateTime.UtcNow.AddHours(1),
            tokenType,
            kioskId = kioskCode is null ? (Guid?)null : Guid.NewGuid(),
            kioskCode,
        });

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task A_brand_new_kiosk_generates_a_key_registers_and_shows_a_pairing_code()
    {
        var (service, handler, keys) = Build();
        var deviceId = Guid.NewGuid();

        keys.Exists.Should().BeFalse("a fresh installation carries no identity at all");

        handler
            .Respond("/api/devices/register", HttpStatusCode.OK, RegistrationJson(deviceId, false, "K7QM-4XPT"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "nonce-1", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("provisioning"))
            .RespondStatus("/api/devices/me/printers", HttpStatusCode.NoContent);

        var state = await service.AdvanceAsync();

        state.Lifecycle.Should().Be(KioskLifecycle.Pending);
        state.PairingCode.Should().Be("K7QM-4XPT");
        state.DeviceId.Should().Be(deviceId);
        state.ActivationQr.Should().Be($"neoprint://activate/{deviceId}?code=K7QM-4XPT");

        keys.Exists.Should().BeTrue("the kiosk generates its own key on first start");
    }

    [Fact]
    public async Task Waiting_for_activation_asks_the_backend_only_what_it_has_to()
    {
        var (service, handler, _) = Build();
        var deviceId = Guid.NewGuid();

        // First start: register, authenticate, report the printers.
        handler
            .Respond("/api/devices/register", HttpStatusCode.OK, RegistrationJson(deviceId, false, "K7QM-4XPT"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "nonce-1", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("provisioning"))
            .RespondStatus("/api/devices/me/printers", HttpStatusCode.NoContent);

        (await service.AdvanceAsync()).PairingCode.Should().Be("K7QM-4XPT");

        handler.Requests.Clear();

        // Then it waits, asking every few seconds whether an administrator has
        // activated it. Only the state endpoint answers here: any other request
        // has no scripted response and would throw.
        for (var poll = 0; poll < 6; poll++)
        {
            handler.Respond("/api/devices/me", HttpStatusCode.OK, StateJson(deviceId, "Pending"));

            var state = await service.AdvanceAsync();

            state.Lifecycle.Should().Be(KioskLifecycle.Pending);
            state.PairingCode.Should().Be("K7QM-4XPT", "the code on screen must not change while it is valid");
        }

        // Registering again would burn the ten-per-hour registration limit, and
        // a fresh token every few seconds burns the ten-per-minute auth limit -
        // which is what stopped the Activation screen ever showing a code.
        handler.Requests.Should().NotContain("POST /api/devices/register");
        handler.Requests.Should().NotContain("POST /api/devices/challenge");
        handler.Requests.Should().NotContain("POST /api/devices/token");
        handler.Requests.Should().OnlyContain(request => request == "GET /api/devices/me");
    }

    [Fact]
    public async Task An_activation_that_happens_while_waiting_is_picked_up()
    {
        var (service, handler, _) = Build();
        var deviceId = Guid.NewGuid();

        handler
            .Respond("/api/devices/register", HttpStatusCode.OK, RegistrationJson(deviceId, false, "K7QM-4XPT"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "nonce-1", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("provisioning"))
            .RespondStatus("/api/devices/me/printers", HttpStatusCode.NoContent);

        await service.AdvanceAsync();

        handler.Requests.Clear();

        // The administrator activates it; the next poll sees the new state and
        // exchanges the provisioning token for a kiosk one.
        handler
            .Respond("/api/devices/me", HttpStatusCode.OK, StateJson(deviceId, "Active", "KSK-0042"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "nonce-2", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("kiosk", "KSK-0042"));

        var state = await service.AdvanceAsync();

        state.Lifecycle.Should().Be(KioskLifecycle.Active);
        state.KioskCode.Should().Be("KSK-0042");
        state.PairingCode.Should().BeNull("an activated kiosk has nothing to pair");
    }

    [Fact]
    public async Task The_same_key_is_reused_on_the_next_start()
    {
        var (service, handler, keys) = Build();
        var deviceId = Guid.NewGuid();

        handler
            .Respond("/api/devices/register", HttpStatusCode.OK, RegistrationJson(deviceId, false, "AAAA-BBBB"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "n", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("provisioning"))
            .RespondStatus("/api/devices/me/printers", HttpStatusCode.NoContent);

        await service.AdvanceAsync();
        var first = keys.GetOrCreatePublicKey();

        // A restart, an update or a reinstall that left the key alone: the
        // identity must survive, or every reboot would need activating again
        // (plan section 7.11).
        var second = keys.GetOrCreatePublicKey();

        second.Should().Be(first);
    }

    [Fact]
    public async Task An_activated_kiosk_reports_its_code_and_stops_showing_a_pairing_code()
    {
        var (service, handler, _) = Build();
        var deviceId = Guid.NewGuid();

        handler
            .Respond("/api/devices/register", HttpStatusCode.OK, RegistrationJson(deviceId, true, null))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "n", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("kiosk", "KSK-0042"));

        var state = await service.AdvanceAsync();

        state.Lifecycle.Should().Be(KioskLifecycle.Active);
        state.KioskCode.Should().Be("KSK-0042");
        state.PairingCode.Should().BeNull();
        state.ActivationQr.Should().BeNull();
    }

    [Fact]
    public async Task An_enrollment_token_is_sent_and_then_deleted_once_it_has_been_used()
    {
        var (service, handler, _) = Build();
        var deviceId = Guid.NewGuid();

        _paths.EnsureCreated();
        var tokenPath = Path.Combine(_paths.DeviceDirectory, DeviceEnrollmentService.EnrollmentTokenFileName);
        await File.WriteAllTextAsync(tokenPath, "enrollment-token-value");

        handler
            .Respond("/api/devices/register", HttpStatusCode.OK, RegistrationJson(deviceId, true, null))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "n", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("kiosk", "KSK-0042"));

        var state = await service.AdvanceAsync();

        state.Lifecycle.Should().Be(KioskLifecycle.Active);
        File.Exists(tokenPath).Should().BeFalse(
            "a spent token must not sit on disk where a later reinstall could reuse it");
    }

    [Fact]
    public async Task A_revoked_device_discards_its_identity_so_the_next_start_is_a_new_device()
    {
        var (service, handler, keys) = Build();

        // Register once, so there is a key and a spool to discard.
        handler
            .Respond("/api/devices/register", HttpStatusCode.OK,
                RegistrationJson(Guid.NewGuid(), false, "AAAA-BBBB"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "n", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("provisioning"))
            .RespondStatus("/api/devices/me/printers", HttpStatusCode.NoContent);

        await service.AdvanceAsync();
        keys.Exists.Should().BeTrue();

        // The admin detaches the device while it is sitting on the activation
        // screen. The device policy checks on every request that the device is
        // still active, so the next poll is refused.
        handler.RespondStatus("/api/devices/me", HttpStatusCode.Forbidden);

        var state = await service.AdvanceAsync();

        state.Lifecycle.Should().Be(KioskLifecycle.Revoked);
        keys.Exists.Should().BeFalse(
            "plan section 7.11: a revoked device clears its identity and returns to the activation screen");

        // And the loop carries on: the very next pass registers as a brand new
        // device and shows a code. Without clearing the device id along with
        // the key, this asks about the revoked device for ever - the screen
        // says Activation and never shows anything to type in.
        var newDeviceId = Guid.NewGuid();

        handler
            .Respond("/api/devices/register", HttpStatusCode.OK,
                RegistrationJson(newDeviceId, false, "CCCC-DDDD"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "n2", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("provisioning"))
            .RespondStatus("/api/devices/me/printers", HttpStatusCode.NoContent);

        var afterwards = await service.AdvanceAsync();

        afterwards.Lifecycle.Should().Be(KioskLifecycle.Pending);
        afterwards.DeviceId.Should().Be(newDeviceId);
        afterwards.PairingCode.Should().Be("CCCC-DDDD");
        keys.Exists.Should().BeTrue("a new key is generated for the new identity");
    }

    [Fact]
    public async Task A_key_the_backend_has_revoked_is_discarded_on_the_next_start()
    {
        var (service, handler, keys) = Build();

        // A fresh process with a key from before the revocation: the identity
        // is not in memory, so this registers - and the backend refuses.
        handler.RespondStatus("/api/devices/register", HttpStatusCode.Conflict);

        var state = await service.AdvanceAsync();

        state.Lifecycle.Should().Be(KioskLifecycle.Revoked);
        keys.Exists.Should().BeFalse("the local identity is worthless once the backend refuses it");
    }

    [Fact]
    public async Task Printers_are_reported_while_waiting_so_the_admin_can_pick_one()
    {
        var (service, handler, _) = Build(new FakePrinterService(
        [
            new DetectedPrinter("HP LaserJet", "hpcups", true, false, true, false),
        ]));

        handler
            .Respond("/api/devices/register", HttpStatusCode.OK,
                RegistrationJson(Guid.NewGuid(), false, "AAAA-BBBB"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "n", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("provisioning"))
            .RespondStatus("/api/devices/me/printers", HttpStatusCode.NoContent);

        var state = await service.AdvanceAsync();

        state.Printers.Should().ContainSingle(printer => printer.Name == "HP LaserJet");
        handler.Requests.Should().Contain("PUT /api/devices/me/printers");
    }

    [Fact]
    public async Task A_printer_subsystem_that_will_not_answer_does_not_stop_registration()
    {
        var handler = new ScriptedHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://backend.test/") };
        var keys = new FakeDeviceKeyStore(_paths);

        var service = new DeviceEnrollmentService(
            new KioskApiClient(http, keys, NullLogger<KioskApiClient>.Instance),
            keys,
            _paths,
            new FakeMachineFingerprintProvider(),
            new ThrowingPrinterService(),
            Options.Create(new KioskSettings()),
            NullLogger<DeviceEnrollmentService>.Instance);

        handler
            .Respond("/api/devices/register", HttpStatusCode.OK,
                RegistrationJson(Guid.NewGuid(), false, "AAAA-BBBB"))
            .Respond("/api/devices/challenge", HttpStatusCode.OK,
                JsonSerializer.Serialize(new { nonce = "n", expiresAt = DateTime.UtcNow.AddMinutes(5) }))
            .Respond("/api/devices/token", HttpStatusCode.OK, TokenJson("provisioning"));

        var state = await service.AdvanceAsync();

        // A kiosk that cannot see its printers can still be activated; the
        // admin refreshes the list afterwards.
        state.Lifecycle.Should().Be(KioskLifecycle.Pending);
        state.Printers.Should().BeEmpty();
    }

    [Fact]
    public void The_device_key_signs_in_the_format_the_backend_verifies()
    {
        var keys = new FakeDeviceKeyStore(_paths);

        var publicKey = keys.GetOrCreatePublicKey();
        var signature = keys.Sign("challenge-nonce");

        // Verified here exactly as the backend verifies it: SPKI public key,
        // SHA-256, IEEE P1363 signature format.
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

        verifier.VerifyData(
                Encoding.UTF8.GetBytes("challenge-nonce"),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue();
    }

    [Fact]
    public void A_signature_over_a_different_nonce_does_not_verify()
    {
        var keys = new FakeDeviceKeyStore(_paths);

        var publicKey = keys.GetOrCreatePublicKey();
        var signature = keys.Sign("nonce-one");

        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

        verifier.VerifyData(
                Encoding.UTF8.GetBytes("nonce-two"),
                Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeFalse();
    }

    [Fact]
    public void The_generated_key_is_ecdsa_p256_as_the_plan_requires()
    {
        var keys = new FakeDeviceKeyStore(_paths);

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(keys.GetOrCreatePublicKey()), out _);

        key.KeySize.Should().Be(256);
    }

    [Theory]
    [InlineData("K7QM-4XPT", "neoprint://activate/{0}?code=K7QM-4XPT")]
    [InlineData(null, "neoprint://activate/{0}")]
    public void The_activation_qr_matches_the_documented_format(string? code, string expected)
    {
        var deviceId = Guid.NewGuid();

        DeviceEnrollmentService.BuildActivationQr(deviceId, code)
            .Should().Be(string.Format(expected, deviceId));
    }

    [Fact]
    public void The_platform_reported_is_the_one_the_process_is_running_on()
    {
        var platform = DeviceEnrollmentService.CurrentPlatform;

        platform.Should().BeOneOf("Windows", "Linux", "Unknown");

        if (OperatingSystem.IsWindows())
        {
            platform.Should().Be("Windows");
        }
        else if (OperatingSystem.IsLinux())
        {
            platform.Should().Be("Linux");
        }
    }

    private sealed class ThrowingPrinterService : IPrinterService
    {
        public Task<IReadOnlyList<DetectedPrinter>> DiscoverAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The print subsystem is not answering.");

        public Task<PrinterState> GetStateAsync(string printer, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The print subsystem is not answering.");

        public Task<PrintOutcome> PrintAsync(PrintRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The print subsystem is not answering.");
    }
}
