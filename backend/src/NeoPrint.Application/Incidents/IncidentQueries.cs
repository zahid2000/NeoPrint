using MediatR;

using NeoPrint.Application.Common;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Incidents;

namespace NeoPrint.Application.Incidents;

/// <summary>
/// Incidents for the panel, filtered by status and type (plan section 8A.3).
/// </summary>
/// <remarks>
/// Read only: the "Fiskal çeklər" page shows the open fiscal incidents next to
/// the receipts they are about. Acknowledging and resolving them, and the
/// incidents page itself, come with step 7.
/// </remarks>
public sealed record ListIncidentsQuery(
    IReadOnlyCollection<string>? Statuses, IReadOnlyCollection<string>? Types, int Page, int PageSize)
    : IRequest<IncidentPage>;

/// <param name="Details">What happened and what to do, in the reader's language.</param>
/// <param name="EntityType">What it is about (<c>FiscalReceipt</c>, <c>Refund</c>, <c>PrintJob</c>), with <see cref="EntityId"/>.</param>
public sealed record IncidentView(
    Guid Id,
    string Type,
    string Severity,
    string Status,
    string Details,
    string? EntityType,
    Guid? EntityId,
    Guid? KioskId,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    string? Resolution)
{
    public static IncidentView From(Incident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        return new IncidentView(
            incident.Id,
            incident.Type.ToString(),
            incident.Severity.ToString(),
            incident.Status.ToString(),
            Messages.WriteStored(incident.Details) ?? string.Empty,
            incident.EntityType,
            incident.EntityId,
            incident.KioskId,
            incident.CreatedAt,
            incident.ResolvedAt,
            Messages.WriteStored(incident.Resolution));
    }
}

public sealed record IncidentPage(IReadOnlyList<IncidentView> Items, long Total, int Page, int PageSize);

public sealed class IncidentQueryHandlers : IRequestHandler<ListIncidentsQuery, IncidentPage>
{
    private readonly IIncidentRepository _incidents;

    public IncidentQueryHandlers(IIncidentRepository incidents) => _incidents = incidents;

    /// <inheritdoc cref="ListIncidentsQuery" />
    public async Task<IncidentPage> Handle(ListIncidentsQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var statuses = Parse<IncidentStatus>(request.Statuses, "status");
        var types = Parse<IncidentType>(request.Types, "type");
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 100);
        var (items, total) = await _incidents.ListAsync(statuses, types, (page - 1) * size, size, cancellationToken).ConfigureAwait(false);

        return new IncidentPage([.. items.Select(IncidentView.From)], total, page, size);
    }

    private static List<T>? Parse<T>(IReadOnlyCollection<string>? values, string field)
        where T : struct, Enum
        => values?
            .Select(value => Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new ValidationException([new ValidationError(field, "incidents.filterInvalid")]))
            .ToList();
}
