using System.Net.Http.Json;

namespace NeoPrint.Kiosk.Core;

// ---------------------------------------------------------------------------
// Wire contracts: the personal QR and the files prepared for a kiosk (plan section 9.4)
// ---------------------------------------------------------------------------

/// <summary>The options the user already chose in the app; a null one is chosen here.</summary>
public sealed record PreparedPreset(
    int? Copies = null,
    bool? Color = null,
    bool? Duplex = null,
    string? Paper = null,
    int? PagesPerSheet = null,
    string? PageRange = null);

/// <summary>One thing the user got ready for a kiosk.</summary>
/// <param name="Kind"><c>Prepared</c> (unpaid, priced here) or <c>Paid</c> (paid in the app for one kiosk).</param>
/// <param name="Here">False for an order paid for another kiosk: shown, not printable here.</param>
/// <param name="KioskCode">The kiosk a paid order belongs to.</param>
public sealed record UserQrItem(
    string Kind,
    Guid Id,
    string FileName,
    int Pages,
    PreparedPreset? Preset,
    JobOptions? Options,
    decimal? Amount,
    string? Currency,
    bool Here,
    string? KioskCode,
    DateTime CreatedAt)
{
    public bool IsPaid => Kind == "Paid";
}

/// <param name="Grant">What lets this kiosk act on the user's files for a few minutes; never stored.</param>
public sealed record UserQrFiles(Guid Grant, DateTime ExpiresAt, IReadOnlyList<UserQrItem> Items);

public sealed partial class KioskApiClient
{
    private const string GrantHeader = "X-User-Qr-Grant";

    /// <summary>
    /// The scanner read a personal QR (<c>NPU:...</c>): the backend checks it
    /// and answers with the user's files and a grant. The code goes in the
    /// body, never in the address.
    /// </summary>
    public async Task<UserQrFiles> ScanUserQrAsync(string code, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "api/print-jobs/pending-for-user", cancellationToken)
            .ConfigureAwait(false);

        request.Content = JsonContent.Create(new { code }, options: JsonOptions);
        return await ReadAsync<UserQrFiles>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An unpaid preparation, ordered here with the options chosen on screen.</summary>
    public async Task<PrintJobInfo> OrderPreparedAsync(
        Guid grant, Guid preparedId, JobOptions options, string idempotencyKey, string? promoCode, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post, $"api/print-jobs/pending-for-user/prepared/{preparedId}", cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Add(GrantHeader, grant.ToString());
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(new { options, promoCode }, options: JsonOptions);
        return await ReadAsync<PrintJobInfo>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>An order paid in the app for this kiosk: into the queue. Asking twice changes nothing.</summary>
    public async Task<PrintJobInfo> ReleaseHeldAsync(Guid grant, Guid jobId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post, $"api/print-jobs/pending-for-user/paid/{jobId}/release", cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Add(GrantHeader, grant.ToString());
        return await ReadAsync<PrintJobInfo>(request, cancellationToken).ConfigureAwait(false);
    }
}
