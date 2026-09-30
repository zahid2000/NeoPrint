using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NeoPrint.Kiosk.Core;

// ---------------------------------------------------------------------------
// Wire contracts: payments, promo codes, receipts, refunds (plan sections 6, 9.1, 9.2)
// ---------------------------------------------------------------------------

/// <summary>A payment as the kiosk that opened it sees it.</summary>
/// <param name="Status">Pending, Succeeded, Failed, Expired, …</param>
/// <param name="QrContent">What the QR shows: the bank's string, or <c>neoprint://pay/{id}</c> for the app; null once it is closed.</param>
/// <param name="PromoRefusal">Why a promo code on the order did not apply to this payment, in the kiosk's language.</param>
public sealed record PaymentInfo(
    Guid Id,
    Guid JobId,
    string JobCode,
    string Method,
    string Status,
    decimal Amount,
    string Currency,
    string? QrContent,
    DateTime ExpiresAt,
    int SecondsLeft,
    DateTime? PaidAt,
    decimal Discount = 0,
    string? PromoCode = null,
    string? PromoRefusal = null)
{
    public bool IsPaid => Status is "Succeeded" or "PartiallyRefunded" or "Refunded" or "RefundFailed";

    public bool IsOpen => Status == "Pending";
}

/// <summary>A payment's fiscal receipt, for the QR on the receipt screen.</summary>
public sealed record FiscalInfo(Guid ReceiptId, string Status, string? FiscalDocumentNumber, string? FiscalId, string? VerificationUrl, DateTime? IssuedAt)
{
    public bool IsIssued => Status == "Issued" && VerificationUrl is not null;
}

/// <summary>What goes back for a job (RefundInfo).</summary>
/// <param name="Destination">Card, Wallet, Voucher or Bonus.</param>
public sealed record RefundInfo(Guid Id, decimal Amount, string Currency, string Status, string Destination, string StatusUrl, string? VoucherCode);

/// <summary>What a promo code takes off, or why not.</summary>
public sealed record PromoValidation(string Code, bool Valid, decimal Discount, decimal AmountDue, string? ReasonCode, string? Reason);

public sealed partial class KioskApiClient
{
    /// <summary>
    /// Opens a payment for a job: <c>BankQr</c> or <c>MobileApp</c>. The same
    /// method again returns the open one; another method closes it first.
    /// </summary>
    public async Task<PaymentInfo> CreatePaymentIntentAsync(Guid jobId, string method, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "api/payments/intents", cancellationToken).ConfigureAwait(false);

        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(new { jobId, method }, options: JsonOptions);

        return await ReadAsync<PaymentInfo>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The polling fallback to <c>PaymentConfirmed</c>.</summary>
    public Task<PaymentInfo> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default)
        => GetAsync<PaymentInfo>($"api/payments/{paymentId}", cancellationToken);

    /// <summary>The payment's fiscal receipt; null until it exists.</summary>
    public async Task<FiscalInfo?> GetPaymentFiscalAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync<FiscalInfo>($"api/payments/{paymentId}/fiscal", cancellationToken).ConfigureAwait(false);
        }
        catch (KioskApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>"Qəbzi email-ə göndər" on the receipt screen: the receipt's id is the payment's.</summary>
    public async Task EmailReceiptAsync(Guid paymentId, string email, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, $"api/kiosks/me/receipts/{paymentId}/email", cancellationToken)
            .ConfigureAwait(false);

        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(new { email }, options: JsonOptions);

        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What goes back for a job, for the RefundInfo screen.</summary>
    public Task<IReadOnlyList<RefundInfo>> GetJobRefundsAsync(Guid jobId, CancellationToken cancellationToken = default)
        => GetAsync<IReadOnlyList<RefundInfo>>($"api/print-jobs/{jobId}/refunds", cancellationToken);

    /// <summary>"Promokodu yoxla" before the order is placed.</summary>
    public async Task<PromoValidation> ValidatePromoAsync(string code, Guid? kioskId, decimal amount, CancellationToken cancellationToken = default)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "api/promos/validate", cancellationToken).ConfigureAwait(false);

        request.Content = JsonContent.Create(new { code, kioskId, amount }, options: JsonOptions);

        return await ReadAsync<PromoValidation>(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A flash order paid with the app: the file off the drive joins the
    /// account (plan section 9.2, step 5). Sending it again is harmless.
    /// </summary>
    public async Task FlashUploadAsync(Guid jobId, Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, $"api/print-jobs/{jobId}/flash-upload", cancellationToken)
            .ConfigureAwait(false);

        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var form = new MultipartFormDataContent { { file, "file", fileName } };
        request.Content = form;

        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
