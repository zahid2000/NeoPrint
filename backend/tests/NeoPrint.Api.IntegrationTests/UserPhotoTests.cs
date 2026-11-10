using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using MediatR;

using Microsoft.Extensions.DependencyInjection;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Privacy;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// The profile picture (T-396; docs/DECISIONS.md 2026-11-11): its owner's
/// only, taken as the phone gives it, erased with a deleted account.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UserPhotoTests
{
    /// <summary>A 1 × 1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly NeoPrintApiFixture _fixture;

    public UserPhotoTests(NeoPrintApiFixture fixture) => _fixture = fixture;

    public static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes, string mediaType = "image/png")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(part, "photo", "me.png");
        return client.PutAsync("/api/users/me/photo", form);
    }

    [DockerFact]
    public async Task The_picture_is_its_owners_only_replaced_removed_and_erased_with_the_account()
    {
        if (_fixture.Skipped)
        {
            return;
        }

        using var user = await TestUsers.ConfirmedAsync(_fixture);
        using var other = await TestUsers.ConfirmedAsync(_fixture);
        (await user.Client.GetAsync("/api/users/me/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Not a picture, whatever the request says.
        var text = await UploadAsync(user.Client, "hello"u8.ToArray());
        text.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await text.Content.ReadAsStringAsync()).Should().Contain("users.photoNotImage");

        var set = await UploadAsync(user.Client, Png);
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        var version = (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt64();

        var mine = await user.Client.GetAsync("/api/users/me/photo");
        mine.StatusCode.Should().Be(HttpStatusCode.OK);
        mine.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await mine.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
        mine.Headers.CacheControl!.NoStore.Should().BeTrue("a face is not kept in a shared cache");

        // Somebody else sees only their own (none).
        (await other.Client.GetAsync("/api/users/me/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A new one replaces it, under a new version.
        var again = await (await UploadAsync(user.Client, Png)).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("version").GetInt64().Should().BeGreaterThan(version);

        (await user.Client.DeleteAsync("/api/users/me/photo")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await user.Client.GetAsync("/api/users/me/photo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await user.Client.DeleteAsync("/api/users/me/photo")).StatusCode.Should().Be(HttpStatusCode.NoContent, "twice is harmless");

        // A deleted account keeps it until it is erased, then it goes.
        (await UploadAsync(user.Client, Png)).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var scope = _fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var account = (await users.FindByIdAsync(user.Id))!;
            account.Delete(DateTime.UtcNow);
            await users.UpdateAsync(account);

            await scope.ServiceProvider.GetRequiredService<ISender>().Send(new EraseAccountCommand(user.Id));
            (await users.FindByIdAsync(user.Id))!.Photo.Should().BeNull();
        }
    }
}
