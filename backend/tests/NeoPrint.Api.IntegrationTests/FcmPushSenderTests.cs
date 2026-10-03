using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using NeoPrint.Application.Notifications;
using NeoPrint.Infrastructure.Notifications;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Our own FCM HTTP v1 client (plan section 2.0) against a fake Google: the
/// signed assertion, the cached access token, and what FCM's answers mean.
/// </summary>
public sealed class FcmPushSenderTests
{
    private static readonly PushMessage Message = new(
        "Çap hazırdır", "100001 sifarişi çap olundu.", new Dictionary<string, string> { ["type"] = "JobCompleted", ["link"] = "neoprint://jobs/1" });

    [Fact]
    public async Task It_signs_in_with_the_service_account_once_and_sends_the_v1_message()
    {
        using var rsa = RSA.Create(2048);
        var google = new FakeGoogle();
        using var sender = Sender(rsa, google);

        (await sender.SendAsync("device-token-1", Message)).Should().Be(PushResult.Delivered);
        (await sender.SendAsync("device-token-2", Message)).Should().Be(PushResult.Delivered);

        // One access token for both pushes.
        google.TokenRequests.Should().ContainSingle();
        var form = google.TokenRequests[0];
        form.Should().Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer");

        // The assertion is RS256 over header.claims with the service account's key.
        var assertion = Uri.UnescapeDataString(form.Split("assertion=")[1]);
        var parts = assertion.Split('.');
        parts.Should().HaveCount(3);
        rsa.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue();
        using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
        header.RootElement.GetProperty("alg").GetString().Should().Be("RS256");
        header.RootElement.GetProperty("kid").GetString().Should().Be("key-1");
        using var claims = JsonDocument.Parse(FromBase64Url(parts[1]));
        claims.RootElement.GetProperty("iss").GetString().Should().Be("push@test-project.iam.gserviceaccount.com");
        claims.RootElement.GetProperty("scope").GetString().Should().Be("https://www.googleapis.com/auth/firebase.messaging");
        claims.RootElement.GetProperty("aud").GetString().Should().Be("https://oauth2.example.test/token");
        (claims.RootElement.GetProperty("exp").GetInt64() - claims.RootElement.GetProperty("iat").GetInt64()).Should().Be(3600);

        // The message: to the project's v1 endpoint, with the bearer token, the notification and the data.
        var send = google.Sends[0];
        send.Url.Should().Be("https://fcm.googleapis.com/v1/projects/test-project/messages:send");
        send.Authorization.Should().Be("Bearer access-1");
        using var body = JsonDocument.Parse(send.Body);
        var message = body.RootElement.GetProperty("message");
        message.GetProperty("token").GetString().Should().Be("device-token-1");
        message.GetProperty("notification").GetProperty("title").GetString().Should().Be("Çap hazırdır");
        message.GetProperty("data").GetProperty("link").GetString().Should().Be("neoprint://jobs/1");
        message.GetProperty("android").GetProperty("priority").GetString().Should().Be("HIGH");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", "UNREGISTERED", PushResult.TokenGone)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_ARGUMENT", "INVALID_ARGUMENT", PushResult.TokenGone)]
    [InlineData(HttpStatusCode.Forbidden, "PERMISSION_DENIED", "SENDER_ID_MISMATCH", PushResult.TokenGone)]
    [InlineData(HttpStatusCode.TooManyRequests, "RESOURCE_EXHAUSTED", "QUOTA_EXCEEDED", PushResult.Failed)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "UNAVAILABLE", "UNAVAILABLE", PushResult.Failed)]
    public async Task FCM_answers_become_delivered_gone_or_try_again(HttpStatusCode status, string grpc, string code, PushResult expected)
    {
        using var rsa = RSA.Create(2048);
        var google = new FakeGoogle { SendStatus = status, SendError = (grpc, code) };
        using var sender = Sender(rsa, google);

        (await sender.SendAsync("device-token", Message)).Should().Be(expected);
    }

    [Fact]
    public async Task An_expired_access_token_is_replaced_once()
    {
        using var rsa = RSA.Create(2048);
        var google = new FakeGoogle { UnauthorizedOnce = true };
        using var sender = Sender(rsa, google);

        (await sender.SendAsync("device-token", Message)).Should().Be(PushResult.Delivered);
        google.TokenRequests.Should().HaveCount(2);
        google.Sends.Select(s => s.Authorization).Should().Equal("Bearer access-1", "Bearer access-2");
    }

    [Fact]
    public async Task A_key_google_refuses_or_an_unreachable_FCM_is_tried_again_later()
    {
        using var rsa = RSA.Create(2048);
        using var refused = Sender(rsa, new FakeGoogle { TokenStatus = HttpStatusCode.BadRequest });
        (await refused.SendAsync("device-token", Message)).Should().Be(PushResult.Failed);

        using var unreachable = Sender(rsa, new FakeGoogle { Throw = true });
        (await unreachable.SendAsync("device-token", Message)).Should().Be(PushResult.Failed);
    }

    [Fact]
    public void A_broken_key_is_named_without_being_quoted()
    {
        using var rsa = RSA.Create(2048);

        FcmServiceAccount.TryParse(null, out var none).Should().BeNull();
        none.Should().BeNull("no key is not a problem: pushes are logged instead");

        FcmServiceAccount.TryParse("{not json", out var problem).Should().BeNull();
        problem.Should().Be("not JSON");

        FcmServiceAccount.TryParse("""{"type":"authorized_user"}""", out problem).Should().BeNull();
        problem.Should().Contain("type");

        var secret = "-----BEGIN PRIVATE KEY-----\nTk9UQUtFWQ==\n-----END PRIVATE KEY-----\n";
        FcmServiceAccount.TryParse(Key(secret), out problem).Should().BeNull();
        problem.Should().Be("private_key is not an RSA key in PEM").And.NotContain("TK9UQUtFWQ");

        var account = FcmServiceAccount.TryParse(Key(rsa.ExportPkcs8PrivateKeyPem()), out problem);
        problem.Should().BeNull();
        account!.ProjectId.Should().Be("test-project");
        account.TokenUri.Should().Be(new Uri("https://oauth2.example.test/token"));
    }

    private static FcmPushSender Sender(RSA rsa, FakeGoogle google)
        => new(
            FcmServiceAccount.TryParse(Key(rsa.ExportPkcs8PrivateKeyPem()), out _)!,
            new FakeHttpClientFactory(google),
            TimeProvider.System,
            NullLogger<FcmPushSender>.Instance);

    private static string Key(string pem) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "service_account",
        ["project_id"] = "test-project",
        ["private_key_id"] = "key-1",
        ["private_key"] = pem,
        ["client_email"] = "push@test-project.iam.gserviceaccount.com",
        ["token_uri"] = "https://oauth2.example.test/token",
    });

    private static byte[] FromBase64Url(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }

    private sealed record Send(string Url, string? Authorization, string Body);

    /// <summary>Google's token endpoint and FCM, as far as the client sees them.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        private int _tokens;

        public List<string> TokenRequests { get; } = [];

        public List<Send> Sends { get; } = [];

        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;

        public HttpStatusCode SendStatus { get; init; } = HttpStatusCode.OK;

        public (string Status, string Code)? SendError { get; init; }

        public bool UnauthorizedOnce { get; set; }

        public bool Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new HttpRequestException("connection refused");
            }

            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            if (request.RequestUri!.Host == "oauth2.example.test")
            {
                TokenRequests.Add(body);
                return TokenStatus != HttpStatusCode.OK
                    ? new HttpResponseMessage(TokenStatus) { Content = new StringContent("""{"error":"invalid_grant"}""") }
                    : Json(HttpStatusCode.OK, $$"""{"access_token":"access-{{++_tokens}}","expires_in":3599,"token_type":"Bearer"}""");
            }

            Sends.Add(new Send(request.RequestUri.ToString(), request.Headers.Authorization?.ToString(), body));

            if (UnauthorizedOnce)
            {
                UnauthorizedOnce = false;
                return Json(HttpStatusCode.Unauthorized, """{"error":{"code":401,"status":"UNAUTHENTICATED"}}""");
            }

            return SendError is { } error
                ? Json(SendStatus, $$$"""{"error":{"code":{{{(int)SendStatus}}},"status":"{{{error.Status}}}","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"{{{error.Code}}}"}]}}""")
                : Json(HttpStatusCode.OK, """{"name":"projects/test-project/messages/1"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string json)
            => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
