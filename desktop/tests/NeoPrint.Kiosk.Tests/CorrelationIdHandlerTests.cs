using System.Net;

using NeoPrint.Kiosk.Core;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk puts a correlation id on every request it makes
/// (<c>docs/OBSERVABILITY.md</c>).
/// </summary>
public sealed class CorrelationIdHandlerTests
{
    /// <summary>Answers every request and remembers what it was sent.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, RecordingHandler Recorder) CreateClient()
    {
        var recorder = new RecordingHandler();
        var handler = new CorrelationIdHandler { InnerHandler = recorder };

        return (new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, recorder);
    }

    [Fact]
    public async Task Every_request_carries_a_correlation_id()
    {
        var (client, recorder) = CreateClient();
        using var _ = client;

        await client.GetAsync("api/kiosks/me/config");

        var sent = recorder.Requests.Single();

        sent.Headers.TryGetValues(CorrelationIdHandler.HeaderName, out var values).Should().BeTrue();
        values!.Single().Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public async Task Each_request_gets_its_own_id()
    {
        var (client, recorder) = CreateClient();
        using var _ = client;

        await client.GetAsync("api/kiosks/me/config");
        await client.GetAsync("api/kiosks/me/config");

        // One id per attempt, not one per session: correlating everything a
        // customer did at the kiosk into a single id would defeat the purpose.
        var ids = recorder.Requests
            .Select(request => request.Headers.GetValues(CorrelationIdHandler.HeaderName).Single())
            .ToList();

        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task An_id_the_caller_already_set_is_left_alone()
    {
        var (client, recorder) = CreateClient();
        using var _ = client;

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/print-jobs");
        request.Headers.TryAddWithoutValidation(CorrelationIdHandler.HeaderName, "job-attempt-7");

        await client.SendAsync(request);

        // The caller is tying this request to work that started earlier.
        recorder.Requests
            .Single()
            .Headers.GetValues(CorrelationIdHandler.HeaderName)
            .Single()
            .Should().Be("job-attempt-7");
    }
}
