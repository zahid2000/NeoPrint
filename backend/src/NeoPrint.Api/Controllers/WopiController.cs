using System.Globalization;
using System.Text.Json;

using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Application.Office;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// The WOPI host Collabora Online reads and saves files through (plan section
/// 2.2, Addım 9; T-459). Called by Collabora inside the stack only: the
/// panel's nginx passes <c>/api</c>, <c>/hubs</c> and Collabora's own paths,
/// never <c>/wopi</c>. Every call carries the session's <c>access_token</c>
/// (WOPI puts it in the query; request URLs are not logged at this level).
/// </summary>
[ApiController]
[Route("wopi/files/{id:guid}")]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class WopiController : ControllerBase
{
    /// <summary>WOPI's names exactly as written: no camelCase.</summary>
    private static readonly JsonSerializerOptions Exact = new();

    private readonly ISender _sender;

    public WopiController(ISender sender) => _sender = sender;

    /// <summary>CheckFileInfo.</summary>
    [HttpGet]
    public async Task<IActionResult> CheckFileInfo(Guid id, [FromQuery(Name = "access_token")] string? token, CancellationToken cancellationToken)
    {
        if (await _sender.Send(new WopiFileInfoQuery(token, id), cancellationToken) is not { } info)
        {
            return Unauthorized();
        }

        var fields = new Dictionary<string, object>
        {
            ["BaseFileName"] = info.BaseFileName,
            ["Size"] = info.Size,
            ["OwnerId"] = info.OwnerId,
            ["UserId"] = info.UserId,
            ["UserFriendlyName"] = info.UserFriendlyName,
            ["Version"] = info.Version,
            ["LastModifiedTime"] = info.LastModifiedTime.ToString("O", CultureInfo.InvariantCulture),
            ["UserCanWrite"] = info.UserCanWrite,
            ["SupportsUpdate"] = true,
            ["SupportsLocks"] = false,
            ["UserCanNotWriteRelative"] = true,
            ["UserCanRename"] = false,
            // Printed at a kiosk, priced and paid there: not from the editor.
            ["DisablePrint"] = true,
            ["HidePrintOption"] = true,
            ["DisableExport"] = true,
            ["HideExportOption"] = true,
            ["DisableCopy"] = false,
            ["EnableShare"] = false,
            ["DisableInactiveMessages"] = true,
        };

        // The app listens on the editor page for "Doc_ModifiedStatus" (T-465).
        if (info.PostMessageOrigin is { } origin)
        {
            fields["PostMessageOrigin"] = origin;
        }

        return new JsonResult(fields, Exact);
    }

    /// <summary>GetFile.</summary>
    [HttpGet("contents")]
    public async Task<IActionResult> GetFile(Guid id, [FromQuery(Name = "access_token")] string? token, CancellationToken cancellationToken)
    {
        if (await _sender.Send(new WopiGetFileQuery(token, id), cancellationToken) is not { } content)
        {
            return Unauthorized();
        }

        return File(content.Content, content.MediaType);
    }

    /// <summary>PutFile (<c>X-WOPI-Override: PUT</c>).</summary>
    [HttpPost("contents")]
    [RequestSizeLimit(200L * 1024 * 1024)]
    public async Task<IActionResult> PutFile(Guid id, [FromQuery(Name = "access_token")] string? token, CancellationToken cancellationToken)
    {
        if (!string.Equals(Request.Headers["X-WOPI-Override"], "PUT", StringComparison.OrdinalIgnoreCase))
        {
            return StatusCode(StatusCodes.Status501NotImplemented);
        }

        // A save of a document nobody changed - leaving the editor, a "Save"
        // with nothing typed - would be a new version and a conversion for nothing.
        if (string.Equals(Request.Headers["X-COOL-WOPI-IsModifiedByUser"], "false", StringComparison.OrdinalIgnoreCase))
        {
            return Ok();
        }

        var result = await _sender.Send(new WopiPutFileCommand(token, id, Request.Body), cancellationToken);

        return result switch
        {
            WopiPutResult.Saved => Ok(),
            WopiPutResult.Unauthorized => Unauthorized(),
            _ => Conflict(),
        };
    }

    /// <summary>
    /// Locks, PutRelativeFile and the rest: not offered (<c>SupportsLocks</c>
    /// and <c>UserCanNotWriteRelative</c> say so), so not done.
    /// </summary>
    [HttpPost]
    public IActionResult Other() => StatusCode(StatusCodes.Status501NotImplemented);
}
