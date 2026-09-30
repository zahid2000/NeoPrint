using MediatR;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using NeoPrint.Api.Infrastructure;
using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Common;
using NeoPrint.Application.Payments;
using NeoPrint.Domain.Payments;
using NeoPrint.Infrastructure.Payments;

namespace NeoPrint.Api.Controllers;

/// <summary>
/// Payments (plan section 6, "Payments"): the kiosk opens an intent and
/// watches it; the provider calls back.
/// </summary>
[ApiController]
[Route("api/payments")]
public sealed class PaymentsController : ControllerBase
{
    /// <summary>A callback is a few hundred bytes; anything much larger is not one.</summary>
    private const int MaxCallbackBytes = 64 * 1024;

    private readonly ISender _sender;
    private readonly ICurrentUser _currentUser;
    private readonly IPaymentProviders _providers;

    public PaymentsController(ISender sender, ICurrentUser currentUser, IPaymentProviders providers)
    {
        _sender = sender;
        _currentUser = currentUser;
        _providers = providers;
    }

    private Guid DeviceId => _currentUser.DeviceId ?? throw new UnauthorizedAccessException();

    private Guid UserId => _currentUser.UserId ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// Opens a payment for a job the kiosk placed: <c>BankQr</c> or
    /// <c>MobileApp</c>. The answer carries the QR and the seconds left.
    /// </summary>
    [HttpPost("intents")]
    [Authorize(Policy = AuthorizationPolicies.Kiosk)]
    [Idempotent]
    public async Task<ActionResult<PaymentResponse>> CreateIntent(
        [FromBody] CreatePaymentIntentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<PaymentMethod>(request.Method, ignoreCase: true, out var method))
        {
            throw new ValidationException([new ValidationError("method", "payments.methodNotAtKiosk")]);
        }

        var key = Request.Headers["Idempotency-Key"].ToString();

        return Ok(await _sender.Send(
            new CreateKioskPaymentIntentCommand(DeviceId, request.JobId, method, key.Length > 0 ? key : null),
            cancellationToken));
    }

    /// <summary>The kiosk's polling fallback while it waits for <c>PaymentConfirmed</c>.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.Kiosk)]
    public async Task<ActionResult<PaymentResponse>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetKioskPaymentQuery(DeviceId, id), cancellationToken));

    /// <summary>
    /// The payment's fiscal receipt (plan section 2.12): status, number and the
    /// verification link for the QR on the kiosk's receipt screen and in the
    /// app. The id is a random GUID; the answer carries no personal data.
    /// <c>404</c> until the receipt exists (nothing paid in money has none).
    /// </summary>
    [HttpGet("{id:guid}/fiscal")]
    [AllowAnonymous]
    public async Task<ActionResult<Application.Fiscal.FiscalSummary>> Fiscal(Guid id, CancellationToken cancellationToken)
        => await _sender.Send(new Application.Fiscal.GetPaymentFiscalQuery(id), cancellationToken) is { } summary
            ? Ok(summary)
            : throw new Domain.Common.NotFoundException("fiscalReceipt");

    /// <summary>
    /// The app scanned the kiosk's <c>neoprint://pay/{id}</c> QR: what is paid
    /// for, and the user's balance and bonus pages (plan section 9.2, step 3).
    /// </summary>
    [HttpGet("{id:guid}/app")]
    [Authorize(Policy = AuthorizationPolicies.User)]
    public async Task<ActionResult<AppPaymentView>> ForApp(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetAppPaymentQuery(UserId, id), cancellationToken));

    /// <summary>
    /// Pays the kiosk's app payment: bonus pages first if asked, the rest from
    /// the wallet or a card. The job, its receipt and the file join the user's account.
    /// </summary>
    [HttpPost("{id:guid}/pay-with-app")]
    [Authorize(Policy = AuthorizationPolicies.User)]
    [Idempotent]
    public async Task<ActionResult<AppPaymentView>> PayWithApp(Guid id, [FromBody] PayWithAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await _sender.Send(new PayWithAppCommand(UserId, id, request.Source ?? string.Empty, request.UseBonus, request.ExpectedAmount), cancellationToken));
    }

    /// <summary>
    /// A provider's callback. Anonymous by nature; trusted only when its
    /// signature holds over the exact bytes sent. The same event twice is
    /// acknowledged and ignored.
    /// </summary>
    [HttpPost("callback/{provider}")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(string provider, CancellationToken cancellationToken)
    {
        if (_providers.Find(provider) is not { } found)
        {
            return NotFound();
        }

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length > MaxCallbackBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var outcome = await _sender.Send(
            new HandlePaymentCallbackCommand(found.Name, buffer.ToArray(), Request.Headers[found.SignatureHeader].ToString()),
            cancellationToken);

        return Ok(new { outcome = outcome.ToString() });
    }
}

/// <param name="Method"><c>BankQr</c> or <c>MobileApp</c>.</param>
public sealed record CreatePaymentIntentRequest(Guid JobId, string Method);

/// <param name="Source"><c>Wallet</c> or <c>Card</c>.</param>
/// <param name="ExpectedAmount">The money part the app showed (after bonus pages and the promo code); optional.</param>
public sealed record PayWithAppRequest(string? Source, bool UseBonus, decimal? ExpectedAmount = null);

/// <summary>
/// The customer and the bank, played in Development (plan section 6,
/// "Dev"). 404 in every other environment.
/// </summary>
[ApiController]
[Route("api/dev")]
[AllowAnonymous]
[DevelopmentOnly]
public sealed class DevPaymentsController : ControllerBase
{
    /// <summary>
    /// The customer pays the bank QR. <c>delaySeconds</c> delays the bank's
    /// callback (late callback); <c>duplicate</c> sends it twice at once;
    /// <c>dropCallback</c> loses it (only reconciliation finds the payment).
    /// </summary>
    [HttpPost("payments/{id:guid}/simulate-success")]
    public async Task<IActionResult> SimulateSuccess(
        Guid id,
        [FromQuery] int delaySeconds,
        [FromQuery] bool duplicate,
        [FromQuery] bool dropCallback,
        CancellationToken cancellationToken)
        => Answer(await Simulator().PayAsync(id, new MockPaymentOptions(Math.Clamp(delaySeconds, 0, 3600), duplicate, dropCallback), cancellationToken));

    /// <summary>The customer's bank declines the payment.</summary>
    [HttpPost("payments/{id:guid}/simulate-fail")]
    public async Task<IActionResult> SimulateFail(Guid id, CancellationToken cancellationToken)
        => Answer(await Simulator().DeclineAsync(id, cancellationToken));

    /// <summary>The bank is down: creating a QR times out until this is turned off again.</summary>
    [HttpPost("mock-bank/create-fails")]
    public async Task<IActionResult> CreateFails([FromQuery] bool fails, CancellationToken cancellationToken)
    {
        await Simulator().SetCreateFailsAsync(fails, cancellationToken);
        return Ok(new { createFails = fails });
    }

    /// <summary>How the mock card acquirer's next charges end: <c>Success</c>, <c>Decline</c> or <c>Timeout</c> (money taken, answer lost).</summary>
    [HttpPost("mock-card/outcome")]
    public async Task<IActionResult> CardOutcome([FromQuery] MockCardOutcome value, CancellationToken cancellationToken)
    {
        await HttpContext.RequestServices.GetRequiredService<MockCardProvider>().SetOutcomeAsync(value, cancellationToken);
        return Ok(new { outcome = value.ToString() });
    }

    private MockBankSimulator Simulator() => HttpContext.RequestServices.GetRequiredService<MockBankSimulator>();

    private IActionResult Answer(MockBankOutcome outcome) => outcome switch
    {
        MockBankOutcome.Done => Ok(new { outcome = outcome.ToString() }),
        MockBankOutcome.Unknown => NotFound(new { outcome = outcome.ToString() }),
        MockBankOutcome.NoCallbackSecret => StatusCode(StatusCodes.Status503ServiceUnavailable, new { outcome = outcome.ToString() }),
        _ => Conflict(new { outcome = outcome.ToString() }),
    };
}
