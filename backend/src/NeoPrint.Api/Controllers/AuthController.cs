using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Auth;
using NeoPrint.Domain.Users;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Registration and sign-in for mobile users (plan sections 2.8, 6).
/// </summary>
[ApiController]
[Route("api/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;

    public AuthController(ISender sender, ICurrentUser currentUser)
    {
        _sender = sender;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Registers a user. A six-digit confirmation code is emailed; there is no
    /// token until it has been entered.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RegistrationResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegistrationResult>> Register(
        [FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(
            new RegisterUserCommand(
                request.Email,
                request.Password,
                request.FullName,
                request.Language ?? "az",
                request.ReferralCode,
                request.DeviceId,
                request.Consents ?? [],
                _currentUser.Ip),
            cancellationToken);

        return Ok(result);
    }

    /// <summary>Confirms an email address and signs the user in.</summary>
    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResult>> ConfirmEmail(
        [FromBody] ConfirmEmailRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new ConfirmEmailCommand(request.Email, request.Code, request.DeviceId), cancellationToken));

    /// <summary>Sends a fresh confirmation code.</summary>
    [HttpPost("resend-confirmation")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendConfirmation(
        [FromBody] ResendConfirmationRequest request, CancellationToken cancellationToken)
    {
        await _sender.Send(new ResendConfirmationCommand(request.Email), cancellationToken);

        // Always the same answer, so this cannot be used to find out which
        // addresses are registered.
        return Accepted();
    }

    /// <summary>Signs a user in.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResult>> Login(
        [FromBody] LoginRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new LoginUserCommand(request.Email, request.Password, request.DeviceId), cancellationToken));

    /// <summary>Exchanges a refresh token for a new pair.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResult>> Refresh(
        [FromBody] RefreshRequest request, CancellationToken cancellationToken)
        => Ok(await _sender.Send(
            new RefreshTokenCommand(request.RefreshToken, request.DeviceId), cancellationToken));

    /// <summary>Revokes a refresh token.</summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(
        [FromBody] RefreshRequest request, CancellationToken cancellationToken)
    {
        await _sender.Send(new LogoutCommand(request.RefreshToken), cancellationToken);
        return NoContent();
    }

    public sealed record RegisterRequest(
        string Email,
        string Password,
        string? FullName,
        string? Language,
        string? ReferralCode,
        string? DeviceId,
        IReadOnlyCollection<ConsentAcceptance>? Consents);

    public sealed record ConfirmEmailRequest(string Email, string Code, string? DeviceId);

    public sealed record ResendConfirmationRequest(string Email);

    public sealed record LoginRequest(string Email, string Password, string? DeviceId);

    public sealed record RefreshRequest(string RefreshToken, string? DeviceId);
}

/// <summary>
/// Legal documents and consents (plan sections 2.13, 6).
/// </summary>
[ApiController]
[Route("api")]
public sealed class LegalController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ILegalDocumentRepository _legal;
    private readonly ICurrentUser _currentUser;

    public LegalController(ISender sender, ILegalDocumentRepository legal, ICurrentUser currentUser)
    {
        _sender = sender;
        _legal = legal;
        _currentUser = currentUser;
    }

    /// <summary>The current version of a legal document.</summary>
    [HttpGet("legal/{docType}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(LegalDocumentResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LegalDocumentResponse>> Get(
        LegalDocumentType docType,
        [FromQuery] string lang = "az",
        CancellationToken cancellationToken = default)
    {
        var document = await _legal.GetCurrentAsync(docType, lang, cancellationToken);

        if (document is null && lang != "az")
        {
            // Not translated yet: show the Azerbaijani text rather than nothing.
            document = await _legal.GetCurrentAsync(docType, "az", cancellationToken);
        }

        return document is null
            ? NotFound()
            : Ok(new LegalDocumentResponse(
                document.DocType, document.Version, document.Language, document.Content, document.PublishedAt));
    }

    /// <summary>Documents the signed-in user still has to accept.</summary>
    [HttpGet("consents/pending")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyCollection<PendingConsent>>> Pending(
        [FromQuery] string lang = "az", CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.UserId;
        if (userId is null)
        {
            return Forbid();
        }

        var current = await _legal.GetCurrentAsync(lang, cancellationToken);
        var accepted = await _legal.GetAcceptedVersionsAsync(userId.Value, cancellationToken);

        var pending = current
            .Where(entry => entry.Key is LegalDocumentType.Privacy or LegalDocumentType.Terms)
            .Where(entry => !accepted.TryGetValue(entry.Key, out var version) ||
                            !string.Equals(version, entry.Value.Version, StringComparison.Ordinal))
            .Select(entry => new PendingConsent(entry.Key, entry.Value.Version))
            .ToList();

        return Ok(pending);
    }

    /// <summary>Records the user's acceptance of the current versions.</summary>
    [HttpPost("consents")]
    [Idempotent]
    [Authorize]
    public async Task<IActionResult> Accept(
        [FromBody] AcceptConsentsRequest request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId;
        if (userId is null)
        {
            return Forbid();
        }

        await _sender.Send(
            new AcceptConsentsCommand(userId.Value, request.Consents, request.DeviceId, _currentUser.Ip),
            cancellationToken);

        return NoContent();
    }

    public sealed record LegalDocumentResponse(
        LegalDocumentType DocType, string Version, string Language, string Content, DateTime PublishedAt);

    public sealed record AcceptConsentsRequest(
        IReadOnlyCollection<ConsentAcceptance> Consents, string? DeviceId);
}
