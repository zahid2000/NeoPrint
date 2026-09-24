using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Admin;

/// <summary>
/// A published version of a legal document (plan section 2.13).
/// </summary>
/// <param name="IsCurrent">Whether this is the newest version for its type and language.</param>
/// <param name="Acceptances">How many users have accepted this exact version.</param>
public sealed record LegalDocumentResponse(
    Guid Id,
    LegalDocumentType DocType,
    string Version,
    string Language,
    string Content,
    DateTime PublishedAt,
    Guid? PublishedBy,
    bool IsCurrent,
    long Acceptances);

/// <summary>Every published version, newest first.</summary>
/// <remarks>
/// The history stays: a consent record points at the version that was accepted,
/// so removing an old version would leave those records referring to nothing
/// (<c>docs/DATA_RETENTION.md</c>).
/// </remarks>
public sealed record ListLegalDocumentsQuery : IRequest<IReadOnlyList<LegalDocumentResponse>>;

/// <summary>
/// Publishes a new version of a document (plan section 2.13).
/// </summary>
/// <remarks>
/// Publishing adds a version rather than editing one. Users who accepted the
/// previous version are asked to accept the new one at their next sign-in, and
/// that only works if both versions continue to exist.
/// </remarks>
public sealed record PublishLegalDocumentCommand(
    LegalDocumentType DocType,
    string Version,
    string Language,
    string Content,
    Guid? PublishedBy,
    string? Ip) : IRequest<LegalDocumentResponse>, ITransactionalRequest;

public sealed class PublishLegalDocumentCommandValidator : IValidator<PublishLegalDocumentCommand>
{
    /// <summary>The languages the plan supports (section 2.1).</summary>
    private static readonly string[] Languages = ["az", "en", "ru"];

    public IEnumerable<ValidationError> Validate(PublishLegalDocumentCommand request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            yield return new ValidationError("version", "Versiya tələb olunur.");
        }
        else if (request.Version.Trim().Length > 40)
        {
            yield return new ValidationError("version", "Versiya 40 simvoldan uzun ola bilməz.");
        }

        if (!Languages.Contains(request.Language, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ValidationError("language", "Dil az, en və ya ru olmalıdır.");
        }

        if (string.IsNullOrWhiteSpace(request.Content))
        {
            yield return new ValidationError("content", "Sənədin məzmunu tələb olunur.");
        }
    }
}
