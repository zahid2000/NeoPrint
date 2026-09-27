using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using NeoPrint.Infrastructure.Seeding;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The admin panel's live connection, over the transport a browser uses
/// (plan section 6, <c>/hubs/admin</c>).
/// </summary>
/// <remarks>
/// The panel showed "no connection" on every page: a browser cannot put a
/// header on a WebSocket, so the SignalR client sends the token as
/// <c>?access_token=</c>, and the API only ever looked at the header. The
/// negotiate call worked - it carries the header - which is why nothing
/// server-side looked wrong. These tests connect the way the browser does,
/// down to the SignalR handshake over a real WebSocket.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class AdminHubConnectionTests
{
    /// <summary>SignalR's record separator, which ends every JSON-protocol message.</summary>
    private const char RecordSeparator = '\u001e';

    private readonly NeoPrintApiFixture _fixture;

    public AdminHubConnectionTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    private async Task<string> AdminTokenAsync()
    {
        using var client = _fixture.CreateApiClient();

        var login = await client.PostAsJsonAsync("/api/auth/admin/login", new
        {
            email = DevelopmentSeeder.TestAdmins[0].Email,
            password = NeoPrintApiFixture.DevTestPassword,
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());

        return (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    [DockerFact]
    public async Task A_browser_connects_over_a_websocket_with_the_token_in_the_query()
    {
        var token = await AdminTokenAsync();
        using var client = _fixture.CreateApiClient();

        var negotiate = await client.PostAsync(
            $"/hubs/admin/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}", null);

        negotiate.StatusCode.Should().Be(HttpStatusCode.OK, await negotiate.Content.ReadAsStringAsync());

        var connectionToken = (await negotiate.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("connectionToken").GetString();

        var sockets = _fixture.Server.CreateWebSocketClient();
        var uri = new Uri(
            _fixture.Server.BaseAddress,
            $"hubs/admin?id={connectionToken}&access_token={Uri.EscapeDataString(token)}");

        using var socket = await sockets.ConnectAsync(
            new UriBuilder(uri) { Scheme = "ws" }.Uri, CancellationToken.None);

        socket.State.Should().Be(WebSocketState.Open);

        // The SignalR handshake: the server answers an empty object when it
        // accepts the protocol, or an error it would otherwise send.
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}" + RecordSeparator),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

        var buffer = new byte[1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await socket.ReceiveAsync(buffer, timeout.Token);

        Encoding.UTF8.GetString(buffer, 0, received.Count).Should().Be("{}" + RecordSeparator);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
    }

    [DockerFact]
    public async Task A_hub_connection_without_a_token_is_refused()
    {
        using var client = _fixture.CreateApiClient();

        (await client.PostAsync("/hubs/admin/negotiate?negotiateVersion=1", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DockerFact]
    public async Task A_token_in_the_query_opens_nothing_outside_the_hubs()
    {
        // The query is accepted where a browser has no other way; an API call
        // always can send the header, and a token in a URL leaks.
        var token = await AdminTokenAsync();
        using var client = _fixture.CreateApiClient();

        (await client.GetAsync($"/api/admin/settings?access_token={Uri.EscapeDataString(token)}"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
