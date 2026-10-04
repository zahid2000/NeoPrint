using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Auth;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Users;

/// <summary>
/// <c>PUT users/me</c> (plan section 6): the account's language - emails,
/// pushes and the server's messages are written in it, on every device the
/// user signs in on (docs/DECISIONS.md T-152). Only the language for now; the
/// name is not edited in this step.
/// </summary>
public sealed record ChangeMyLanguageCommand(Guid UserId, string? Language) : IRequest;

public sealed class ChangeMyLanguageCommandValidator : IValidator<ChangeMyLanguageCommand>
{
    public IEnumerable<ValidationError> Validate(ChangeMyLanguageCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Language))
        {
            yield return new ValidationError("language", "common.languageInvalid");
            yield break;
        }

        foreach (var error in AuthRules.Language(request.Language))
        {
            yield return error;
        }
    }
}

public sealed class ChangeMyLanguageHandler : IRequestHandler<ChangeMyLanguageCommand>
{
    private readonly IUserRepository _users;
    private readonly ILogger<ChangeMyLanguageHandler> _logger;

    public ChangeMyLanguageHandler(IUserRepository users, ILogger<ChangeMyLanguageHandler> logger)
    {
        _users = users;
        _logger = logger;
    }

    public async Task Handle(ChangeMyLanguageCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false);

        if (user is not { DeletedAt: null })
        {
            throw new NotFoundException("user");
        }

        // The same answer for a repeat: setting the language it already has changes nothing.
        await _users.SetLanguageAsync(user.Id, request.Language!, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("User {UserId} set the account language to {Language}.", user.Id, request.Language);
    }
}
