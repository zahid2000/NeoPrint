using MediatR;

using NeoPrint.Application.Abstractions;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Users;

namespace NeoPrint.Application.Admin;

/// <inheritdoc cref="ListLegalDocumentsQuery" />
public sealed class ListLegalDocumentsHandler
    : IRequestHandler<ListLegalDocumentsQuery, IReadOnlyList<LegalDocumentResponse>>
{
    private readonly ILegalDocumentRepository _legal;

    public ListLegalDocumentsHandler(ILegalDocumentRepository legal) => _legal = legal;

    public async Task<IReadOnlyList<LegalDocumentResponse>> Handle(
        ListLegalDocumentsQuery request, CancellationToken cancellationToken)
    {
        var documents = await _legal.ListAsync(cancellationToken).ConfigureAwait(false);

        // The newest version per type and language is the one in force; the
        // rest are history that consents still point at.
        var current = documents
            .GroupBy(document => (document.DocType, document.Language))
            .Select(group => group.OrderByDescending(document => document.PublishedAt).First().Id)
            .ToHashSet();

        var responses = new List<LegalDocumentResponse>(documents.Count);

        foreach (var document in documents)
        {
            var acceptances = await _legal
                .CountAcceptancesAsync(document.DocType, document.Version, cancellationToken)
                .ConfigureAwait(false);

            responses.Add(new LegalDocumentResponse(
                document.Id,
                document.DocType,
                document.Version,
                document.Language,
                document.Content,
                document.PublishedAt,
                document.PublishedBy,
                current.Contains(document.Id),
                acceptances));
        }

        return responses;
    }
}

/// <inheritdoc cref="PublishLegalDocumentCommand" />
public sealed class PublishLegalDocumentHandler
    : IRequestHandler<PublishLegalDocumentCommand, LegalDocumentResponse>
{
    private readonly ILegalDocumentRepository _legal;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;

    public PublishLegalDocumentHandler(
        ILegalDocumentRepository legal, IAuditWriter audit, IClock clock)
    {
        _legal = legal;
        _audit = audit;
        _clock = clock;
    }

    public async Task<LegalDocumentResponse> Handle(
        PublishLegalDocumentCommand request, CancellationToken cancellationToken)
    {
        var version = request.Version.Trim();
        var language = request.Language.Trim().ToLowerInvariant();

        var existing = await _legal.ListAsync(cancellationToken).ConfigureAwait(false);

        if (existing.Any(document =>
                document.DocType == request.DocType &&
                document.Language == language &&
                string.Equals(document.Version, version, StringComparison.OrdinalIgnoreCase)))
        {
            // Republishing the same version with different text would change
            // what people already accepted, retrospectively.
            throw new DomainException(UserMessage.Of("legal.versionPublished", ("version", version)));
        }

        var document = new LegalDocument
        {
            DocType = request.DocType,
            Version = version,
            Language = language,
            Content = request.Content,
            PublishedAt = _clock.UtcNow,
            PublishedBy = request.PublishedBy,
            CreatedAt = _clock.UtcNow,
        };

        await _legal.AddAsync(document, cancellationToken).ConfigureAwait(false);

        await _audit
            .WriteAsync(
                new AuditEntry(
                    request.PublishedBy,
                    "legal.publish",
                    "LegalDocument",
                    document.Id.ToString(),
                    Before: null,
                    // The text itself is not in the audit entry: it is long,
                    // and it is already stored as the document.
                    After: $"{document.DocType} {document.Version} ({document.Language})",
                    request.Ip,
                    Reason: null,
                    ActorType.Admin),
                cancellationToken)
            .ConfigureAwait(false);

        return new LegalDocumentResponse(
            document.Id,
            document.DocType,
            document.Version,
            document.Language,
            document.Content,
            document.PublishedAt,
            document.PublishedBy,
            IsCurrent: true,
            Acceptances: 0);
    }
}
