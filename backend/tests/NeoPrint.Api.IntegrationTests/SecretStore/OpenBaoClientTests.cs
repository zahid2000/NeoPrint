using System.Net;

using NeoPrint.Infrastructure.SecretStore;

namespace NeoPrint.Api.IntegrationTests.SecretStore;

/// <summary>
/// The OpenBao client driven against a scripted transport: AppRole login,
/// KV v2 reads, token renewal and failure handling (plan sections 2.13, 11).
/// </summary>
public sealed class OpenBaoClientTests
{
    private const string LoginResponse = """
        {"auth":{"client_token":"bao-token-1","lease_duration":3600,"renewable":true}}
        """;

    private static OpenBaoOptions Options() => new()
    {
        Address = "http://openbao.test:8200",
        RoleId = "role-id",
        SecretId = "secret-id",
        KvMount = "neoprint",
    };

    private static (OpenBaoClient Client, FakeHttpMessageHandler Handler) Build(OpenBaoOptions? options = null)
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler);
        return (new OpenBaoClient(http, options ?? Options()), handler);
    }

    [Fact]
    public async Task Logs_in_with_the_approle_credentials_before_the_first_read()
    {
        var (client, handler) = Build();
        handler
            .RespondJson(HttpStatusCode.OK, LoginResponse)
            .RespondJson(HttpStatusCode.OK, """{"data":{"data":{"NEOPRINT_MONGO_URI":"mongodb://x"}}}""");

        await client.ReadAsync("backend/core");

        handler.Requests.Should().HaveCount(2);

        var login = handler.Requests[0];
        login.Method.Should().Be(HttpMethod.Post);
        login.Uri.AbsolutePath.Should().Be("/v1/auth/approle/login");
        login.Body.Should().Contain("role-id").And.Contain("secret-id");

        var read = handler.Requests[1];
        read.Method.Should().Be(HttpMethod.Get);
        read.Uri.AbsolutePath.Should().Be("/v1/neoprint/data/backend/core");
        read.VaultToken.Should().Be("bao-token-1");
    }

    [Fact]
    public async Task Reuses_the_token_across_reads()
    {
        var (client, handler) = Build();
        handler
            .RespondJson(HttpStatusCode.OK, LoginResponse)
            .RespondJson(HttpStatusCode.OK, """{"data":{"data":{"A":"1"}}}""")
            .RespondJson(HttpStatusCode.OK, """{"data":{"data":{"B":"2"}}}""");

        await client.ReadAsync("backend/core");
        await client.ReadAsync("backend/crypto");

        handler.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("approle/login", StringComparison.Ordinal))
            .Should().Be(1, "a valid lease must not be spent on a fresh login for every read");
    }

    [Fact]
    public async Task Returns_the_key_values_at_a_path()
    {
        var (client, handler) = Build();
        handler
            .RespondJson(HttpStatusCode.OK, LoginResponse)
            .RespondJson(HttpStatusCode.OK, """
                {"data":{"data":{"NEOPRINT_MONGO_URI":"mongodb://m","NEOPRINT_REDIS":"redis:6379"},
                "metadata":{"version":3}}}
                """);

        var secrets = await client.ReadAsync("backend/core");

        secrets.Should().HaveCount(2);
        secrets["NEOPRINT_MONGO_URI"].Should().Be("mongodb://m");
        secrets["NEOPRINT_REDIS"].Should().Be("redis:6379");
    }

    [Fact]
    public async Task Flattens_non_string_values_so_they_can_be_used_as_configuration()
    {
        var (client, handler) = Build();
        handler
            .RespondJson(HttpStatusCode.OK, LoginResponse)
            .RespondJson(HttpStatusCode.OK, """
                {"data":{"data":{"PORT":1025,"USE_TLS":false,"EMPTY":null,
                "NEOPRINT_FCM_SERVICE_ACCOUNT_JSON":{"type":"service_account","project_id":"p"}}}}
                """);

        var secrets = await client.ReadAsync("backend/providers");

        secrets["PORT"].Should().Be("1025");
        secrets["USE_TLS"].Should().Be("false");
        secrets["EMPTY"].Should().BeNull();
        // A structured value is kept as its JSON text, which is exactly what
        // the FCM service account key needs.
        secrets["NEOPRINT_FCM_SERVICE_ACCOUNT_JSON"].Should().Contain("service_account");
    }

    [Fact]
    public async Task A_path_that_has_never_been_written_reads_as_empty_not_as_an_error()
    {
        var (client, handler) = Build();
        handler
            .RespondJson(HttpStatusCode.OK, LoginResponse)
            .RespondStatus(HttpStatusCode.NotFound);

        var secrets = await client.ReadAsync("backend/backup");

        secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task Logs_in_again_when_the_token_is_rejected_mid_flight()
    {
        var (client, handler) = Build();
        handler
            .RespondJson(HttpStatusCode.OK, LoginResponse)
            .RespondStatus(HttpStatusCode.Forbidden)
            .RespondJson(HttpStatusCode.OK, """{"auth":{"client_token":"bao-token-2","lease_duration":3600,"renewable":true}}""")
            .RespondJson(HttpStatusCode.OK, """{"data":{"data":{"A":"1"}}}""");

        var secrets = await client.ReadAsync("backend/core");

        secrets["A"].Should().Be("1");
        handler.Requests[^1].VaultToken.Should().Be("bao-token-2");
    }

    [Fact]
    public async Task Renews_a_lease_that_is_about_to_expire_rather_than_logging_in_again()
    {
        var (client, handler) = Build();
        handler
            // A short lease, so the very next read falls inside the renewal window.
            .RespondJson(HttpStatusCode.OK, """{"auth":{"client_token":"t","lease_duration":60,"renewable":true}}""")
            .RespondJson(HttpStatusCode.OK, """{"data":{"data":{"A":"1"}}}""")
            .RespondJson(HttpStatusCode.OK, """{"auth":{"lease_duration":3600,"renewable":true}}""")
            .RespondJson(HttpStatusCode.OK, """{"data":{"data":{"B":"2"}}}""");

        await client.ReadAsync("backend/core");
        await client.ReadAsync("backend/crypto");

        handler.Requests.Select(r => r.Uri.AbsolutePath).Should().ContainInOrder(
            "/v1/auth/approle/login",
            "/v1/neoprint/data/backend/core",
            "/v1/auth/token/renew-self",
            "/v1/neoprint/data/backend/crypto");
    }

    [Fact]
    public async Task Falls_back_to_a_fresh_login_when_renewal_fails()
    {
        var (client, handler) = Build();
        handler
            .RespondJson(HttpStatusCode.OK, """{"auth":{"client_token":"t1","lease_duration":60,"renewable":true}}""")
            .RespondJson(HttpStatusCode.OK, """{"data":{"data":{"A":"1"}}}""")
            .RespondStatus(HttpStatusCode.BadRequest)     // renew-self refused
            .RespondJson(HttpStatusCode.OK, """{"auth":{"client_token":"t2","lease_duration":3600,"renewable":true}}""")
            .RespondJson(HttpStatusCode.OK, """{"data":{"data":{"B":"2"}}}""");

        await client.ReadAsync("backend/core");
        var secrets = await client.ReadAsync("backend/crypto");

        secrets["B"].Should().Be("2");
        handler.Requests[^1].VaultToken.Should().Be("t2");
    }

    [Fact]
    public async Task Reports_an_unreachable_server_without_leaking_the_credentials()
    {
        var (client, handler) = Build();
        handler.RespondConnectionFailure();

        var act = () => client.ReadAsync("backend/core");

        var exception = await act.Should().ThrowAsync<SecretStoreUnavailableException>();
        exception.Which.Message.Should().Contain("openbao.test");
        exception.Which.Message.Should().NotContain("secret-id");
    }

    [Fact]
    public async Task Reports_rejected_credentials_without_echoing_them()
    {
        var (client, handler) = Build();
        handler.RespondJson(HttpStatusCode.BadRequest, """{"errors":["invalid role or secret id: secret-id"]}""");

        var act = () => client.ReadAsync("backend/core");

        var exception = await act.Should().ThrowAsync<SecretStoreUnavailableException>();
        exception.Which.Message.Should().Contain("400");
        exception.Which.Message.Should().NotContain("secret-id",
            "the response body can repeat the credentials back and must not reach the log");
    }

    [Fact]
    public async Task Reports_a_server_error_with_its_status_code()
    {
        var (client, handler) = Build();
        handler
            .RespondJson(HttpStatusCode.OK, LoginResponse)
            .RespondStatus(HttpStatusCode.InternalServerError);

        var act = () => client.ReadAsync("backend/core");

        (await act.Should().ThrowAsync<SecretStoreUnavailableException>())
            .Which.Message.Should().Contain("500");
    }
}
