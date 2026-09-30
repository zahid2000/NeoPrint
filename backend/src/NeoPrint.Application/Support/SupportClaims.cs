using System.Text.Json;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Behaviors;
using NeoPrint.Application.Common;
using NeoPrint.Application.PrintJobs;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.PrintJobs;
using NeoPrint.Domain.Support;

namespace NeoPrint.Application.Support;

/// <summary>The <c>support_claims</c> collection (plan section 2.5, "Müştəri müraciəti").</summary>
public interface ISupportClaimRepository
{
    Task AddAsync(SupportClaim claim, CancellationToken cancellationToken = default);

    Task<SupportClaim?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdateAsync(SupportClaim claim, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupportClaim>> ListForJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupportClaim>> ListForUserAsync(Guid userId, int take, CancellationToken cancellationToken = default);

    /// <summary>Open claims about one job: a customer who writes again adds to the pile only so far.</summary>
    Task<long> CountOpenForJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SupportClaim> Items, long Total)> SearchAsync(
        IReadOnlyCollection<ClaimStatus>? statuses, Guid? jobId, int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>
/// An anonymous customer's claim: the job code and the kiosk code from the
/// receipt or the kiosk's screen (plan section 2.5). Both must match - the
/// answer says nothing about the job either way beyond "received".
/// </summary>
public sealed record OpenAnonymousClaimCommand(string? JobCode, string? KioskCode, string? Text, string? Contact)
    : IRequest<SupportClaimReceipt>, ITransactionalRequest;

/// <summary>
/// A claim from the public refund status page (plan section 2.5): the page
/// knows its refund, and the refund its job - the customer types no codes.
/// </summary>
public sealed record OpenRefundClaimCommand(Guid RefundId, string? Text, string? Contact) : IRequest<SupportClaimReceipt>, ITransactionalRequest;

/// <summary>"Problem barədə bildir" in the app: about one of the user's own jobs.</summary>
public sealed record OpenAppClaimCommand(Guid UserId, Guid JobId, string? Text) : IRequest<SupportClaimView>, ITransactionalRequest;

/// <summary>The user's claims and their answers, newest first.</summary>
public sealed record ListMyClaimsQuery(Guid UserId) : IRequest<IReadOnlyList<SupportClaimView>>;

public sealed record ListClaimsQuery(IReadOnlyCollection<string>? Statuses, Guid? JobId, int Page, int PageSize) : IRequest<SupportClaimPage>;

/// <summary>An admin's answer; the app shows it (push with the mobile step).</summary>
public sealed record AnswerClaimCommand(Guid AdminUserId, Guid ClaimId, string? Reply, string? Ip) : IRequest<SupportClaimView>, ITransactionalRequest;

public sealed record CloseClaimCommand(Guid AdminUserId, Guid ClaimId, string? Ip) : IRequest<SupportClaimView>, ITransactionalRequest;

/// <summary>What an anonymous customer gets back: the claim's id to quote on the hotline.</summary>
public sealed record SupportClaimReceipt(Guid Id, string Status, DateTime CreatedAt);

/// <param name="Contact">Shown to admins only; the user's own view leaves it out.</param>
public sealed record SupportClaimView(
    Guid Id,
    string Channel,
    Guid? JobId,
    string? JobCode,
    Guid? KioskId,
    Guid? UserId,
    string Text,
    string? Contact,
    string Status,
    string? Reply,
    DateTime? RepliedAt,
    DateTime CreatedAt)
{
    public static SupportClaimView From(SupportClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return new SupportClaimView(
            claim.Id,
            claim.Channel.ToString(),
            claim.JobId,
            claim.JobCode,
            claim.KioskId,
            claim.UserId,
            claim.Text,
            claim.Contact,
            claim.Status.ToString(),
            claim.Reply,
            claim.RepliedAt,
            claim.CreatedAt);
    }
}

public sealed record SupportClaimPage(IReadOnlyList<SupportClaimView> Items, long Total, int Page, int PageSize);

public sealed class SupportClaimHandlers :
    IRequestHandler<OpenAnonymousClaimCommand, SupportClaimReceipt>,
    IRequestHandler<OpenRefundClaimCommand, SupportClaimReceipt>,
    IRequestHandler<OpenAppClaimCommand, SupportClaimView>,
    IRequestHandler<ListMyClaimsQuery, IReadOnlyList<SupportClaimView>>,
    IRequestHandler<ListClaimsQuery, SupportClaimPage>,
    IRequestHandler<AnswerClaimCommand, SupportClaimView>,
    IRequestHandler<CloseClaimCommand, SupportClaimView>
{
    /// <summary>Open claims one job may have; more is someone filling the list, not a customer being heard.</summary>
    public const int MaxOpenPerJob = 5;

    private const int MaxContactLength = 200;

    private readonly ISupportClaimRepository _claims;
    private readonly IPrintJobRepository _jobs;
    private readonly IKioskRepository _kiosks;
    private readonly Refunds.IRefundRepository _refunds;
    private readonly IAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ILogger<SupportClaimHandlers> _logger;

    public SupportClaimHandlers(
        ISupportClaimRepository claims,
        IPrintJobRepository jobs,
        IKioskRepository kiosks,
        Refunds.IRefundRepository refunds,
        IAuditWriter audit,
        IClock clock,
        ILogger<SupportClaimHandlers> logger)
    {
        _claims = claims;
        _jobs = jobs;
        _kiosks = kiosks;
        _refunds = refunds;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc cref="OpenAnonymousClaimCommand" />
    public async Task<SupportClaimReceipt> Handle(OpenAnonymousClaimCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Contact is { Length: > MaxContactLength })
        {
            throw new ValidationException([new ValidationError("contact", "support.contactTooLong")]);
        }

        var kiosk = string.IsNullOrWhiteSpace(request.KioskCode)
            ? null
            : await _kiosks.FindByCodeAsync(request.KioskCode.Trim().ToUpperInvariant(), cancellationToken).ConfigureAwait(false);
        var job = kiosk is null || string.IsNullOrWhiteSpace(request.JobCode)
            ? null
            : await _jobs.FindByCodeAsync(kiosk.Id, request.JobCode.Trim(), cancellationToken).ConfigureAwait(false);

        if (job is null)
        {
            // One answer for a wrong job code and a wrong kiosk code: the
            // endpoint does not tell a guesser which half was right.
            throw new NotFoundException("printJob");
        }

        var claim = await OpenAsync(ClaimChannel.Anonymous, job, userId: null, request.Text, request.Contact, cancellationToken).ConfigureAwait(false);

        return new SupportClaimReceipt(claim.Id, claim.Status.ToString(), claim.CreatedAt);
    }

    /// <inheritdoc cref="OpenRefundClaimCommand" />
    public async Task<SupportClaimReceipt> Handle(OpenRefundClaimCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Contact is { Length: > MaxContactLength })
        {
            throw new ValidationException([new ValidationError("contact", "support.contactTooLong")]);
        }

        var refund = await _refunds.FindByIdAsync(request.RefundId, cancellationToken).ConfigureAwait(false);
        var job = refund?.JobId is { } jobId ? await _jobs.FindByIdAsync(jobId, cancellationToken).ConfigureAwait(false) : null;

        if (job is null)
        {
            throw new NotFoundException("refund");
        }

        var claim = await OpenAsync(ClaimChannel.Anonymous, job, userId: null, request.Text, request.Contact, cancellationToken).ConfigureAwait(false);

        return new SupportClaimReceipt(claim.Id, claim.Status.ToString(), claim.CreatedAt);
    }

    /// <inheritdoc cref="OpenAppClaimCommand" />
    public async Task<SupportClaimView> Handle(OpenAppClaimCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = await _jobs.FindByIdAsync(request.JobId, cancellationToken).ConfigureAwait(false);

        if (job is null || job.UserId != request.UserId)
        {
            throw new NotFoundException("printJob");
        }

        var claim = await OpenAsync(ClaimChannel.App, job, request.UserId, request.Text, contact: null, cancellationToken).ConfigureAwait(false);

        return Own(claim);
    }

    /// <inheritdoc cref="ListMyClaimsQuery" />
    public async Task<IReadOnlyList<SupportClaimView>> Handle(ListMyClaimsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var claims = await _claims.ListForUserAsync(request.UserId, 100, cancellationToken).ConfigureAwait(false);

        return [.. claims.Select(Own)];
    }

    /// <inheritdoc cref="ListClaimsQuery" />
    public async Task<SupportClaimPage> Handle(ListClaimsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var statuses = request.Statuses?
            .Select(value => Enum.TryParse<ClaimStatus>(value, out var status) ? status : throw new ValidationException([new ValidationError("status", "support.statusInvalid")]))
            .ToList();
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var (items, total) = await _claims.SearchAsync(statuses, request.JobId, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);

        return new SupportClaimPage([.. items.Select(SupportClaimView.From)], total, page, size);
    }

    /// <inheritdoc cref="AnswerClaimCommand" />
    public async Task<SupportClaimView> Handle(AnswerClaimCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Reply) || request.Reply.Trim().Length > SupportClaim.MaxTextLength)
        {
            throw new ValidationException([new ValidationError("reply", UserMessage.Of("support.textLength", ("max", SupportClaim.MaxTextLength)))]);
        }

        var claim = await _claims.FindByIdAsync(request.ClaimId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("claim");

        if (claim.Status == ClaimStatus.Closed)
        {
            throw new DomainException("support.closed");
        }

        claim.Answer(request.AdminUserId, request.Reply, _clock.UtcNow);
        await _claims.UpdateAsync(claim, cancellationToken).ConfigureAwait(false);
        await AuditAsync(request.AdminUserId, "support.claims.answer", claim, request.Ip, cancellationToken).ConfigureAwait(false);

        return SupportClaimView.From(claim);
    }

    /// <inheritdoc cref="CloseClaimCommand" />
    public async Task<SupportClaimView> Handle(CloseClaimCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var claim = await _claims.FindByIdAsync(request.ClaimId, cancellationToken).ConfigureAwait(false) ?? throw new NotFoundException("claim");

        if (claim.Status != ClaimStatus.Closed)
        {
            claim.Close();
            await _claims.UpdateAsync(claim, cancellationToken).ConfigureAwait(false);
            await AuditAsync(request.AdminUserId, "support.claims.close", claim, request.Ip, cancellationToken).ConfigureAwait(false);
        }

        return SupportClaimView.From(claim);
    }

    private async Task<SupportClaim> OpenAsync(
        ClaimChannel channel, PrintJob job, Guid? userId, string? text, string? contact, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length > SupportClaim.MaxTextLength)
        {
            throw new ValidationException([new ValidationError("text", UserMessage.Of("support.textLength", ("max", SupportClaim.MaxTextLength)))]);
        }

        if (await _claims.CountOpenForJobAsync(job.Id, cancellationToken).ConfigureAwait(false) >= MaxOpenPerJob)
        {
            throw new DomainException("support.tooManyOpen");
        }

        var claim = SupportClaim.Open(channel, job.Id, job.JobCode, job.KioskId, userId, text ?? string.Empty, contact, _clock.UtcNow);

        await _claims.AddAsync(claim, cancellationToken).ConfigureAwait(false);

        // Every claim is in audit_logs (plan section 2.5); the text and the
        // contact stay in the claim, not in the audit or the log.
        await _audit.WriteAsync(
                new AuditEntry(
                    null,
                    "support.claims.open",
                    nameof(SupportClaim),
                    claim.Id.ToString(),
                    null,
                    JsonSerializer.Serialize(new { channel = channel.ToString(), jobId = job.Id, userId }),
                    null,
                    null,
                    userId is null ? ActorType.System : ActorType.User),
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Support claim {ClaimId} opened about job {JobId} ({Channel}).", claim.Id, job.Id, channel);

        return claim;
    }

    private Task AuditAsync(Guid adminId, string action, SupportClaim claim, string? ip, CancellationToken cancellationToken)
        => _audit.WriteAsync(
            new AuditEntry(adminId, action, nameof(SupportClaim), claim.Id.ToString(), null, JsonSerializer.Serialize(new { status = claim.Status.ToString() }), ip, null),
            cancellationToken);

    /// <summary>The customer's own view: their contact is theirs already, the admin's id is not theirs to see.</summary>
    private static SupportClaimView Own(SupportClaim claim) => SupportClaimView.From(claim) with { Contact = null };
}
