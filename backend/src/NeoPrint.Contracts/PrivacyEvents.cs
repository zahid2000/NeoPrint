namespace NeoPrint.Contracts;

/// <summary>A "Məlumatlarımı yüklə" archive was asked for (plan section 6, <c>DataExportJob</c>).</summary>
public sealed record DataExportRequested(Guid ExportId, Guid UserId, string? CorrelationId);
