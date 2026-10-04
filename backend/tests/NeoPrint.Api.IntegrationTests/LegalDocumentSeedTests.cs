using System.Net.Http.Json;
using System.Text.Json;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The seeded legal drafts are shown to users as they are (T-161): no pointer
/// into the repository, which means nothing to someone reading the app.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LegalDocumentSeedTests
{
    private readonly NeoPrintApiFixture _fixture;

    public LegalDocumentSeedTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    [DockerFact]
    public async Task The_seeded_drafts_point_nowhere_into_the_repository()
    {
        if (_fixture.Skipped)
        {
            return;
        }

        using var client = _fixture.CreateApiClient();

        foreach (var docType in new[] { "Privacy", "Terms", "ScanNotice" })
        {
            var document = await client.GetFromJsonAsync<JsonElement>($"/api/legal/{docType}?lang=az");
            var content = document.GetProperty("content").GetString();

            content.Should().NotBeNullOrWhiteSpace(docType);
            content.Should().NotContain("docs/", docType).And.NotContain(".md", docType);
        }
    }
}
