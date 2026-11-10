using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Kiosks;
using NeoPrint.Domain.Admin;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// What kiosks say on their welcome screen and footer, and the picture beside
/// the big line (T-387): the default for every kiosk (<c>settings.manage</c>)
/// and one kiosk's own (<c>kiosks.edit</c>, within the caller's scope).
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminKioskBrandingController : ControllerBase
{
    /// <summary>The picture's limit plus room for the form around it.</summary>
    private const long ImageRequestBytes = KioskPhotoHandlers.MaxBytes + (64 * 1024);

    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminKioskBrandingController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>Signed in as an administrator: <see cref="HasPermissionAttribute"/> saw to that.</summary>
    private Guid AdminId => _currentUser.AdminUserId
        ?? throw new InvalidOperationException("An admin endpoint was reached without an admin user.");

    // ---- the default

    [HttpGet("kiosk-branding")]
    [HasPermission(Permissions.SettingsManage)]
    public async Task<ActionResult<KioskBrandingView>> GetDefault(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetKioskBrandingQuery(null, AdminId), cancellationToken));

    [HttpPut("kiosk-branding")]
    [HasPermission(Permissions.SettingsManage)]
    public async Task<ActionResult<KioskBrandingView>> SetDefault([FromBody] KioskBrandingTextsInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SetKioskBrandingTextsCommand(null, input.Texts(), AdminId, _currentUser.Ip), cancellationToken));

    [HttpPut("kiosk-branding/image")]
    [HasPermission(Permissions.SettingsManage)]
    [RequestSizeLimit(ImageRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageRequestBytes)]
    public async Task<ActionResult<KioskBrandingView>> SetDefaultImage(IFormFile? image, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SetKioskBrandingImageCommand(null, await BytesAsync(image, cancellationToken), AdminId, _currentUser.Ip), cancellationToken));

    [HttpDelete("kiosk-branding/image")]
    [HasPermission(Permissions.SettingsManage)]
    public async Task<ActionResult<KioskBrandingView>> RemoveDefaultImage(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new RemoveKioskBrandingImageCommand(null, AdminId, _currentUser.Ip), cancellationToken));

    [HttpGet("kiosk-branding/image")]
    [HasPermission(Permissions.SettingsManage)]
    public async Task<IActionResult> DefaultImage(CancellationToken cancellationToken)
        => KioskPhotoResults.Of(this, await _sender.Send(new GetKioskBrandingImageQuery(null, AdminId), cancellationToken));

    // ---- one kiosk's own

    [HttpGet("kiosks/{id:guid}/branding")]
    [HasPermission(Permissions.KiosksView)]
    public async Task<ActionResult<KioskBrandingView>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetKioskBrandingQuery(id, AdminId), cancellationToken));

    [HttpPut("kiosks/{id:guid}/branding")]
    [HasPermission(Permissions.KiosksEdit)]
    public async Task<ActionResult<KioskBrandingView>> Set(Guid id, [FromBody] KioskBrandingTextsInput input, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SetKioskBrandingTextsCommand(id, input.Texts(), AdminId, _currentUser.Ip), cancellationToken));

    [HttpPut("kiosks/{id:guid}/branding/image")]
    [HasPermission(Permissions.KiosksEdit)]
    [RequestSizeLimit(ImageRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageRequestBytes)]
    public async Task<ActionResult<KioskBrandingView>> SetImage(Guid id, IFormFile? image, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new SetKioskBrandingImageCommand(id, await BytesAsync(image, cancellationToken), AdminId, _currentUser.Ip), cancellationToken));

    [HttpDelete("kiosks/{id:guid}/branding/image")]
    [HasPermission(Permissions.KiosksEdit)]
    public async Task<ActionResult<KioskBrandingView>> RemoveImage(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new RemoveKioskBrandingImageCommand(id, AdminId, _currentUser.Ip), cancellationToken));

    [HttpGet("kiosks/{id:guid}/branding/image")]
    [HasPermission(Permissions.KiosksView)]
    public async Task<IActionResult> Image(Guid id, CancellationToken cancellationToken)
        => KioskPhotoResults.Of(this, await _sender.Send(new GetKioskBrandingImageQuery(id, AdminId), cancellationToken));

    private static async Task<byte[]> BytesAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return [];
        }

        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}

/// <summary>Key → language → text; an empty text falls back.</summary>
public sealed record KioskBrandingTextsInput(Dictionary<string, Dictionary<string, string?>>? Values)
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Texts()
        => (Values ?? []).ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, string?>)(pair.Value ?? []),
            StringComparer.Ordinal);
}
