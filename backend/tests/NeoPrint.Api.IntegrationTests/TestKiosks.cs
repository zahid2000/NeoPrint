using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// An activated kiosk for a test: registered with a key pair made here,
/// activated by the seeded SuperAdmin, holding a kiosk token - the requests
/// the kiosk application makes (plan section 7.11).
/// </summary>
internal sealed class TestKiosk : IDisposable
{
    public required HttpClient Client { get; init; }

    public required Guid DeviceId { get; init; }

    public required Guid KioskId { get; init; }

    public required string KioskCode { get; init; }

    public required string AccessToken { get; init; }

    public void Dispose() => Client.Dispose();
}

internal static class TestKiosks
{
    /// <summary>A signed-in SuperAdmin client.</summary>
    public static async Task<HttpClient> SuperAdminAsync(NeoPrintApiFixture fixture)
    {
        var client = fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = NeoPrintApiFixture.SeedAdminEmail,
            password = NeoPrintApiFixture.SeedAdminPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        return client;
    }

    /// <summary>
    /// Registers a device with one printer, activates it as a new kiosk with
    /// that printer selected, and signs in as the kiosk.
    /// </summary>
    public static async Task<TestKiosk> ActivatedAsync(
        NeoPrintApiFixture fixture,
        string hostname,
        bool color = true,
        bool duplex = true,
        bool a3 = false,
        int paperA4 = 500,
        bool selectPrinter = true)
    {
        using var anonymous = fixture.CreateApiClient();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var register = await anonymous.PostAsJsonAsync("/api/devices/register", new
        {
            publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            fingerprint = new
            {
                hostname,
                os = "Linux",
                osVersion = "6.1",
                machineId = Guid.NewGuid().ToString("n"),
                macAddresses = new[] { "aa:bb:cc:dd:ee:ff" },
                cpu = "Test CPU",
                ramGb = 8,
                screenResolution = "1920x1080",
            },
            platform = "Linux",
            appVersion = "0.3.0",
            agentVersion = "0.3.0",
            printers = new[]
            {
                new { name = "Test printer", driverName = "Test driver", isOnline = true, color, duplex, a3 },
            },
        });

        register.StatusCode.Should().Be(HttpStatusCode.OK, await register.Content.ReadAsStringAsync());
        var deviceId = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deviceId").GetGuid();

        using var admin = await SuperAdminAsync(fixture);

        var activate = await admin.PostAsJsonAsync($"/api/admin/devices/{deviceId}/activate", new
        {
            newKiosk = new
            {
                name = $"Print test {hostname}",
                lat = 40.3793,
                lng = 49.8486,
                address = "Bakı",
                defaultLanguage = "az",
                paperCapacityA4 = paperA4,
                paperCapacityA3 = 250,
                adminPin = "135790",
            },
            selectedPrinterName = selectPrinter ? "Test printer" : null,
        });

        activate.StatusCode.Should().Be(HttpStatusCode.OK, await activate.Content.ReadAsStringAsync());
        var activated = await activate.Content.ReadFromJsonAsync<JsonElement>();

        var challenge = await anonymous.PostAsJsonAsync("/api/devices/challenge", new { deviceId });
        var nonce = (await challenge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nonce").GetString()!;

        var token = await anonymous.PostAsJsonAsync("/api/devices/token", new
        {
            deviceId,
            nonce,
            signature = Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes(nonce), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        });

        var issued = await token.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = issued.GetProperty("accessToken").GetString()!;

        var client = fixture.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return new TestKiosk
        {
            Client = client,
            DeviceId = deviceId,
            KioskId = activated.GetProperty("kioskId").GetGuid(),
            KioskCode = activated.GetProperty("kioskCode").GetString()!,
            AccessToken = accessToken,
        };
    }
}

/// <summary>
/// A SignalR connection over a real WebSocket, speaking the JSON protocol by
/// hand: enough to see what a hub sends, without the client library.
/// </summary>
internal sealed class RawHubConnection : IAsyncDisposable
{
    private const char RecordSeparator = '\u001e';

    private readonly WebSocket _socket;
    private readonly StringBuilder _pending = new();

    private RawHubConnection(WebSocket socket) => _socket = socket;

    public static async Task<RawHubConnection> ConnectAsync(NeoPrintApiFixture fixture, string hub, string token)
    {
        using var client = fixture.CreateApiClient();

        var negotiate = await client.PostAsync(
            $"/hubs/{hub}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);

        negotiate.StatusCode.Should().Be(HttpStatusCode.OK, await negotiate.Content.ReadAsStringAsync());

        var connectionToken = (await negotiate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connectionToken").GetString();
        var uri = new Uri(fixture.Server.BaseAddress, $"hubs/{hub}?id={connectionToken}&access_token={Uri.EscapeDataString(token)}");

        var socket = await fixture.Server.CreateWebSocketClient()
            .ConnectAsync(new UriBuilder(uri) { Scheme = "ws" }.Uri, CancellationToken.None);

        var connection = new RawHubConnection(socket);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}" + RecordSeparator),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

        (await connection.NextAsync(TimeSpan.FromSeconds(10))).Should().Be("{}", "the server accepts the protocol");

        // The handshake is answered before the hub has put the connection in its
        // groups; an event sent in between is not heard. The admin hub says the
        // account is connected only once it has (T-301: two tests heard nothing
        // of what was sent right after connecting, under load).
        if (hub == "admin")
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            var subject = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=')))
                .RootElement.GetProperty("sub").GetString();
            var connections = fixture.Services.GetRequiredService<NeoPrint.Api.Hubs.AdminConnections>();
            var deadline = DateTime.UtcNow.AddSeconds(15);

            while (!connections.IsOpen(Guid.Parse(subject!)))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The panel connection did not join its groups in 15 seconds.");
                }

                await Task.Delay(50);
            }
        }

        return connection;
    }

    /// <summary>The next invocation of <paramref name="target"/>, as its first argument.</summary>
    public async Task<JsonElement> WaitForAsync(string target, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var message = await NextAsync(deadline - DateTime.UtcNow);
            using var document = JsonDocument.Parse(message);

            if (document.RootElement.TryGetProperty("target", out var name) && name.GetString() == target)
            {
                return document.RootElement.GetProperty("arguments")[0].Clone();
            }
        }

        throw new TimeoutException($"No {target} within {timeout}.");
    }

    /// <summary>
    /// Every invocation that arrives, in order, up to and including the first
    /// one <paramref name="last"/> accepts - what a panel heard, not only
    /// what a test was waiting for.
    /// </summary>
    public async Task<List<(string Target, JsonElement Argument)>> ReadUntilAsync(
        Func<string, JsonElement, bool> last, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var heard = new List<(string Target, JsonElement Argument)>();

        while (DateTime.UtcNow < deadline)
        {
            var message = await NextAsync(deadline - DateTime.UtcNow);
            using var document = JsonDocument.Parse(message);

            if (!document.RootElement.TryGetProperty("target", out var name) || name.GetString() is not { } target)
            {
                continue;
            }

            var arguments = document.RootElement.GetProperty("arguments");
            var argument = arguments.GetArrayLength() > 0 ? arguments[0].Clone() : default;

            heard.Add((target, argument));

            if (last(target, argument))
            {
                return heard;
            }
        }

        throw new TimeoutException($"The awaited message did not come within {timeout}; heard: {string.Join(", ", heard.Select(item => item.Target))}.");
    }

    /// <summary>
    /// Whether the server ended the connection within <paramref name="timeout"/>:
    /// a close frame, or the socket gone.
    /// </summary>
    public async Task<bool> ClosedByServerAsync(TimeSpan timeout)
    {
        var buffer = new byte[64 * 1024];
        using var cancel = new CancellationTokenSource(timeout);

        try
        {
            while (true)
            {
                var received = await _socket.ReceiveAsync(buffer, cancel.Token);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is WebSocketException or IOException or ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Every invocation that arrives within <paramref name="window"/>. The
    /// Redis backplane gives no order between groups, so what was sent to one
    /// group may still come after what was sent later to another. The
    /// connection is not read again afterwards: a cancelled receive ends it.
    /// </summary>
    public async Task<List<(string Target, JsonElement Argument)>> ReadForAsync(TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var heard = new List<(string Target, JsonElement Argument)>();

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                using var document = JsonDocument.Parse(await NextAsync(deadline - DateTime.UtcNow));

                if (document.RootElement.TryGetProperty("target", out var name) && name.GetString() is { } target)
                {
                    var arguments = document.RootElement.GetProperty("arguments");
                    heard.Add((target, arguments.GetArrayLength() > 0 ? arguments[0].Clone() : default));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The window is over.
        }

        return heard;
    }

    private async Task<string> NextAsync(TimeSpan timeout)
    {
        var buffer = new byte[64 * 1024];
        using var cancel = new CancellationTokenSource(timeout);

        while (true)
        {
            var text = _pending.ToString();
            var end = text.IndexOf(RecordSeparator, StringComparison.Ordinal);

            if (end >= 0)
            {
                _pending.Remove(0, end + 1);
                return text[..end];
            }

            var received = await _socket.ReceiveAsync(buffer, cancel.Token);
            _pending.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        _socket.Dispose();
    }
}
