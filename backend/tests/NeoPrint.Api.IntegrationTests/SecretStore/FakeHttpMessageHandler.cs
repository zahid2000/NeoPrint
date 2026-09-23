using System.Net;
using System.Text;

namespace NeoPrint.Api.IntegrationTests.SecretStore;

/// <summary>
/// Hand-written stand-in for an HTTP transport.
/// </summary>
/// <remarks>
/// Plan section 11 rules out a mocking library, so test doubles are written by
/// hand. This one records every request and answers from a queue of scripted
/// responses, which is enough to drive the OpenBao client through login,
/// renewal, reads and failures.
/// </remarks>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    /// <summary>Every request the client made, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Answers the next request with a JSON body.</summary>
    public FakeHttpMessageHandler RespondJson(HttpStatusCode status, string json)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    /// <summary>Answers the next request with a status and no body.</summary>
    public FakeHttpMessageHandler RespondStatus(HttpStatusCode status)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status));
        return this;
    }

    /// <summary>Fails the next request the way an unreachable server does.</summary>
    public FakeHttpMessageHandler RespondConnectionFailure(string message = "Connection refused")
    {
        _responses.Enqueue(_ => throw new HttpRequestException(message));
        return this;
    }

    /// <summary>Answers every further request the same way.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("X-Vault-Token", out var token) ? token.FirstOrDefault() : null,
            body));

        if (_responses.Count > 0)
        {
            return _responses.Dequeue()(request);
        }

        if (Fallback is not null)
        {
            return Fallback(request);
        }

        throw new InvalidOperationException(
            $"The fake handler received an unexpected {request.Method} {request.RequestUri} and has no response left.");
    }

    public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? VaultToken, string? Body);
}
