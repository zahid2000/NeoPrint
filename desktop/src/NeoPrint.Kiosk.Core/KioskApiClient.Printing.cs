using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using NeoPrint.Kiosk.Platform.Abstractions;

namespace NeoPrint.Kiosk.Core;

// ---------------------------------------------------------------------------
// Wire contracts: printers, commands, print jobs (plan sections 6, 7.4–7.8)
// ---------------------------------------------------------------------------

/// <summary>A command the panel sent this kiosk (plan section 7.8).</summary>
public sealed record KioskCommandInfo(Guid Id, string Type, string Status, string? Result, DateTime CreatedAt);

/// <summary>What the customer chose (plan section 2.3); the same shape the backend takes.</summary>
public sealed record JobOptions(
    int Copies = 1,
    string? PageRange = null,
    bool Color = false,
    bool Duplex = false,
    string Paper = "A4",
    string Orientation = "Auto",
    int PagesPerSheet = 1,
    string Scaling = "Fit");

public sealed record JobPrice(
    decimal Total,
    string Currency,
    decimal BasePrice,
    decimal VolumeDiscount,
    decimal DiscountPercent,
    bool MinimumApplied,
    int Sides,
    int Sheets,
    string ProfileName);

/// <summary>A print job as the backend returns it.</summary>
public sealed record PrintJobInfo(
    Guid Id,
    string JobCode,
    Guid KioskId,
    string Status,
    string Source,
    string? FileName,
    int PageCount,
    int SelectedPages,
    JobOptions Options,
    JobPrice Price,
    int RequiredSheets,
    int? QueuePosition,
    int PrintedSides,
    int PrintedSheets,
    string? ErrorReason,
    string? CancelReason,
    bool PaymentSkipped,
    DateTime CreatedAt);

/// <summary>One line of the queue panel (plan section 7.2).</summary>
public sealed record QueueItem(
    Guid JobId,
    string JobCode,
    int Position,
    string Status,
    int Pages,
    int Copies,
    string? UserInitial,
    string? FileName,
    DateTime? QueuedAt);

/// <summary>
/// The backend said no, and said why: the message is already in the
/// language the kiosk asked for (Accept-Language), the code is the rule.
/// </summary>
public sealed class KioskApiException : Exception
{
    public KioskApiException(HttpStatusCode status, string? code, string message)
        : base(message)
    {
        Status = status;
        Code = code;
    }

    public HttpStatusCode Status { get; }

    /// <summary>The message key, e.g. <c>printJobs.notEnoughPaper</c>; null when the backend gave none.</summary>
    public string? Code { get; }
}

public sealed partial class KioskApiClient
{
    /// <summary>A valid kiosk token, renewed when needed: what the hub connection presents.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureTokenAsync(cancellationToken).ConfigureAwait(false);
        return _accessToken;
    }

    /// <summary>Reports the printers this kiosk can see (plan section 7.7).</summary>
    public async Task ReportKioskPrintersAsync(
        IReadOnlyCollection<DetectedPrinter> printers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(printers);

        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Put, "api/kiosks/me/printers", cancellationToken)
            .ConfigureAwait(false);

        request.Content = JsonContent.Create(
            new
            {
                printers = printers.Select(printer => new
                {
                    name = printer.Name,
                    driverName = printer.DriverName,
                    isOnline = printer.IsOnline,
                    color = printer.SupportsColor,
                    duplex = printer.SupportsDuplex,
                    a3 = printer.SupportsA3,
                }),
            },
            options: JsonOptions);

        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Commands sent while this kiosk was not listening.</summary>
    public Task<IReadOnlyList<KioskCommandInfo>> GetOpenCommandsAsync(CancellationToken cancellationToken = default)
        => GetAsync<IReadOnlyList<KioskCommandInfo>>("api/kiosks/me/commands", cancellationToken);

    /// <summary>Answers a command; answering twice is harmless.</summary>
    public async Task CompleteCommandAsync(
        Guid commandId, bool succeeded, string? messageKey, int sheetsUsed, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post, $"api/kiosks/me/commands/{commandId}/result", cancellationToken)
            .ConfigureAwait(false);

        request.Content = JsonContent.Create(new { succeeded, messageKey, sheetsUsed }, options: JsonOptions);

        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Places an order for a file on the flash drive. The key makes a retry
    /// after a lost response return the same job rather than a second one.
    /// </summary>
    public async Task<PrintJobInfo> CreatePrintJobAsync(
        string? fileName, int pageCount, JobOptions options, bool skipPayment, string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "api/print-jobs", cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(
            new { localFileName = fileName, pageCount, options, dev = skipPayment }, options: JsonOptions);

        return await ReadAsync<PrintJobInfo>(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<PrintJobInfo> GetPrintJobAsync(Guid jobId, CancellationToken cancellationToken = default)
        => GetAsync<PrintJobInfo>($"api/print-jobs/{jobId}", cancellationToken);

    /// <summary>The cancel screen's lookup; <see cref="KioskApiException"/> with 404 for an unknown code.</summary>
    public Task<PrintJobInfo> FindPrintJobByCodeAsync(string code, CancellationToken cancellationToken = default)
        => GetAsync<PrintJobInfo>($"api/print-jobs/by-code/{Uri.EscapeDataString(code)}", cancellationToken);

    public async Task<PrintJobInfo> CancelPrintJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, $"api/print-jobs/{jobId}/cancel", cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Add("Idempotency-Key", $"cancel-{jobId:N}");

        return await ReadAsync<PrintJobInfo>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the backend how printing went (plan section 7.4). Repeating a
    /// report is harmless, so a report that may not have arrived is sent again.
    /// </summary>
    public async Task<PrintJobInfo> ReportPrintJobStatusAsync(
        Guid jobId, string status, int printedSides, int printedSheets, string? errorCode,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, $"api/print-jobs/{jobId}/status", cancellationToken)
            .ConfigureAwait(false);

        request.Content = JsonContent.Create(new { status, printedSides, printedSheets, errorCode }, options: JsonOptions);

        return await ReadAsync<PrintJobInfo>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>This kiosk's queue, in order.</summary>
    public Task<IReadOnlyList<QueueItem>> GetQueueAsync(CancellationToken cancellationToken = default)
        => GetAsync<IReadOnlyList<QueueItem>>("api/kiosks/me/queue", cancellationToken);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<T>(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ReadAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        await ThrowIfProblemAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"{request.RequestUri} answered with nothing.");
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        await ThrowIfProblemAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A refusal of the device (403) still means it was detached; anything
    /// else the backend refuses comes back with its reason.
    /// </summary>
    private static async Task ThrowIfProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new DeviceRevokedException("The backend refused this device.");
        }

        string? code = null;
        string? message = null;

        try
        {
            using var problem = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var root = problem.RootElement;
            code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            message = root.TryGetProperty("detail", out var d) ? d.GetString() : null;

            if (message is null && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                message = errors.EnumerateObject()
                    .SelectMany(field => field.Value.EnumerateArray())
                    .Select(item => item.GetString())
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            }

            message ??= root.TryGetProperty("title", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            // Not a problem document (a proxy's page): the status says enough.
        }

        throw new KioskApiException(response.StatusCode, code, message ?? response.ReasonPhrase ?? response.StatusCode.ToString());
    }
}
