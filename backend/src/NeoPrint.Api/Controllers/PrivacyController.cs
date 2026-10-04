using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Privacy;
using NeoPrint.Domain.Common;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// "Məlumatlarımı yüklə" (plan sections 2.13 and 6; <c>docs/DECISIONS.md</c> T-147).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.User)]
public sealed class PrivacyController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;
    private readonly IFileStorage _storage;

    public PrivacyController(ISender sender, ICurrentUser currentUser, IFileStorage storage)
    {
        _sender = sender;
        _currentUser = currentUser;
        _storage = storage;
    }

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// Asks for an archive, made in the background; an <c>ExportReady</c>
    /// notification says when. One still being made or still valid is returned
    /// instead of a second.
    /// </summary>
    [HttpPost("api/users/me/export")]
    public async Task<ActionResult<DataExportView>> RequestExport(CancellationToken cancellationToken)
        => Ok(await _sender.Send(new RequestDataExportCommand(UserId), cancellationToken));

    /// <summary>The latest archive; <c>downloadUrl</c> while it is ready. <c>204</c> when there is none.</summary>
    [HttpGet("api/users/me/export")]
    public async Task<ActionResult<DataExportView>> GetExport(CancellationToken cancellationToken)
        => await _sender.Send(new GetDataExportQuery(UserId), cancellationToken) is { } view ? Ok(view) : NoContent();

    /// <summary>
    /// The archive itself, for the system browser: the signed link stands in
    /// for the session and is good until the archive expires.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("api/exports/download")]
    public async Task<IActionResult> Download([FromQuery] string? token, CancellationToken cancellationToken)
    {
        var export = await _sender.Send(new ResolveExportDownloadQuery(token ?? string.Empty), cancellationToken);
        if (export?.Archive is not { } archive)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: Application.Localization.Messages.Write(new UserMessage("privacy.exportLinkInvalid")));
        }

        var stream = await _storage.OpenReadAsync(DataExportHandlers.Stored(archive), StorageBucket.Temp, cancellationToken);
        return File(stream, "application/zip", $"neoprint-{export.ReadyAt:yyyy-MM-dd}.zip", enableRangeProcessing: false);
    }
}
