using System.Net;

using NeoPrint.Kiosk.Core;

namespace NeoPrint.Kiosk.Tests;

/// <summary>
/// The kiosk asks the backend for its messages in the language the screen is
/// showing (docs/TASKS.md T-043).
/// </summary>
[Collection(KioskLanguageCollection.Name)]
public sealed class AcceptLanguageHandlerTests
{
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

    [Fact]
    public async Task A_request_carries_the_language_on_screen_and_follows_a_switch()
    {
        var recorder = new RecordingHandler();
        using var client = new HttpClient(new AcceptLanguageHandler { InnerHandler = recorder })
        {
            BaseAddress = new Uri("http://localhost/"),
        };

        try
        {
            KioskLanguage.Current = "ru";
            await client.GetAsync("api/kiosks/me/config");

            KioskLanguage.Current = "en";
            await client.GetAsync("api/kiosks/me/config");

            recorder.Requests[0].Headers.AcceptLanguage.Single().Value.Should().Be("ru");
            recorder.Requests[1].Headers.AcceptLanguage.Single().Value.Should().Be("en");
        }
        finally
        {
            KioskLanguage.Current = "az";
        }
    }
}

/// <summary>Tests that change the process-wide screen language run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KioskLanguageCollection
{
    public const string Name = "Kiosk language";
}
