using MediatR;

using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Users;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Platform settings and legal documents (plan sections 2.4, 2.13, 8A.3).
/// </summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminConfigController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AdminConfigController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    // -----------------------------------------------------------------------
    // Settings
    // -----------------------------------------------------------------------

    /// <summary>Every setting, with its documented default alongside.</summary>
    [HttpGet("settings")]
    [HasPermission(Permissions.SettingsManage)]
    public async Task<ActionResult<IReadOnlyList<SettingResponse>>> ListSettings(
        CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListSettingsQuery(), cancellationToken));

    /// <summary>
    /// Changes settings.
    /// </summary>
    /// <remarks>
    /// Several at once, because the panel saves a page rather than a field. An
    /// unknown key or a value of the wrong type is refused before anything is
    /// written.
    /// </remarks>
    [HttpPut("settings")]
    [HasPermission(Permissions.SettingsManage)]
    public async Task<ActionResult<IReadOnlyList<SettingResponse>>> UpdateSettings(
        [FromBody] UpdateSettingsRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new UpdateSettingsCommand(request.Values, _currentUser.AdminUserId, _currentUser.Ip),
            cancellationToken));

    // -----------------------------------------------------------------------
    // Legal documents
    // -----------------------------------------------------------------------

    /// <summary>Every published version, newest first.</summary>
    [HttpGet("legal-documents")]
    [HasPermission(Permissions.LegalManage)]
    public async Task<ActionResult<IReadOnlyList<LegalDocumentResponse>>> ListLegalDocuments(
        CancellationToken cancellationToken)
        => Ok(await _sender.Send(new ListLegalDocumentsQuery(), cancellationToken));

    /// <summary>
    /// Publishes a new version.
    /// </summary>
    /// <remarks>
    /// Adds a version rather than editing one: users who accepted the previous
    /// text are asked to accept the new one, and their consent records point at
    /// the version they actually saw (plan section 2.13).
    /// </remarks>
    [HttpPost("legal-documents")]
    [HasPermission(Permissions.LegalManage)]
    [Idempotent]
    public async Task<ActionResult<LegalDocumentResponse>> PublishLegalDocument(
        [FromBody] PublishLegalDocumentRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new PublishLegalDocumentCommand(
                request.DocType,
                request.Version,
                request.Language,
                request.Content,
                _currentUser.AdminUserId,
                _currentUser.Ip),
            cancellationToken));

    public sealed record UpdateSettingsRequest(IReadOnlyDictionary<string, string> Values);

    public sealed record PublishLegalDocumentRequest(
        LegalDocumentType DocType, string Version, string Language, string Content);
}
