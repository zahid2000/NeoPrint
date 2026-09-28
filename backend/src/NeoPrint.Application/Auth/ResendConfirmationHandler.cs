using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Localization;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Auth;

/// <summary>
/// Sends a fresh email confirmation code.
/// </summary>
/// <remarks>
/// Always reports success, whatever the address turns out to be. Anything else
/// would let an anonymous caller test which addresses are registered.
/// </remarks>
public sealed class ResendConfirmationHandler : IRequestHandler<ResendConfirmationCommand, Unit>
{
    /// <summary>
    /// Shortest gap between two codes for the same account, so this endpoint
    /// cannot be used to flood someone's inbox.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly IUserRepository _users;
    private readonly IEmailConfirmationRepository _confirmations;
    private readonly ISecretHasher _secrets;
    private readonly IEmailSender _email;
    private readonly IClock _clock;
    private readonly ILogger<ResendConfirmationHandler> _logger;

    public ResendConfirmationHandler(
        IUserRepository users,
        IEmailConfirmationRepository confirmations,
        ISecretHasher secrets,
        IEmailSender email,
        IClock clock,
        ILogger<ResendConfirmationHandler> logger)
    {
        _users = users;
        _confirmations = confirmations;
        _secrets = secrets;
        _email = email;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Unit> Handle(ResendConfirmationCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);
        var user = await _users.FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);

        if (user is null || user.EmailConfirmed || !user.CanSignIn)
        {
            _logger.LogInformation("A confirmation code was requested for an address with nothing to confirm.");
            return Unit.Value;
        }

        var latest = await _confirmations.FindLatestPendingAsync(user.Id, cancellationToken).ConfigureAwait(false);

        if (latest is not null && _clock.UtcNow - latest.CreatedAt < MinimumInterval)
        {
            _logger.LogInformation("A confirmation code was requested again too soon; ignoring.");
            return Unit.Value;
        }

        var code = EmailConfirmation.NewCode();

        await _confirmations.AddAsync(
                new EmailConfirmation
                {
                    UserId = user.Id,
                    CodeHash = _secrets.HashSecret(code),
                    ExpiresAt = _clock.UtcNow + RegisterUserHandler.ConfirmationLifetime,
                    CreatedAt = _clock.UtcNow,
                },
                cancellationToken)
            .ConfigureAwait(false);

        await _email.SendAsync(
                user.Email,
                Messages.Write(new UserMessage("email.confirm.subject"), user.Language),
                Messages.Write(
                    UserMessage.Of(
                        "email.confirmAgain.body",
                        ("code", code),
                        ("minutes", (int)RegisterUserHandler.ConfirmationLifetime.TotalMinutes)),
                    user.Language),
                cancellationToken)
            .ConfigureAwait(false);

        return Unit.Value;
    }
}
