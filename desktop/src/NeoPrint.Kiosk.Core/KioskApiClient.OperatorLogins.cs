using System.Net.Http.Json;

namespace NeoPrint.Kiosk.Core;

/// <summary>The QR the admin corner shows (T-392): <c>neoprint://kiosk-login/{code}</c>, for two minutes.</summary>
public sealed record OperatorLoginStarted(Guid Id, string Qr, DateTime ExpiresAt);

/// <summary>
/// Whether an operator confirmed the QR, in whose name the menu is open, and
/// what that account may do here now (T-392).
/// </summary>
/// <param name="Status">Pending, Approved, Ended or Expired.</param>
public sealed record OperatorLoginState(
    Guid Id,
    string Status,
    string? OperatorName,
    DateTime ExpiresAt,
    bool CanSelectPrinter,
    bool CanChangeQueue,
    bool CanPause)
{
    public bool IsApproved => string.Equals(Status, "Approved", StringComparison.Ordinal);

    public bool IsExpired => string.Equals(Status, "Expired", StringComparison.Ordinal)
                             || string.Equals(Status, "Ended", StringComparison.Ordinal);
}

/// <summary>An operator at the kiosk by the kiosk's QR (T-392): the admin menu's side of it.</summary>
public interface IOperatorLogins
{
    Task<OperatorLoginStarted> StartAsync(CancellationToken cancellationToken);

    Task<OperatorLoginState> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <param name="action">SelectPrinter, SetQueueLimit, Pause or Resume.</param>
    Task ActAsync(Guid id, string action, string? printer, int? limit, CancellationToken cancellationToken);

    Task EndAsync(Guid id, CancellationToken cancellationToken);
}

public sealed partial class KioskApiClient : IOperatorLogins
{
    public async Task<OperatorLoginStarted> StartAsync(CancellationToken cancellationToken)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, "api/kiosks/me/operator-logins", cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync<OperatorLoginStarted>(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperatorLoginState> GetAsync(Guid id, CancellationToken cancellationToken)
        => GetAsync<OperatorLoginState>($"api/kiosks/me/operator-logins/{id}", cancellationToken);

    public async Task ActAsync(Guid id, string action, string? printer, int? limit, CancellationToken cancellationToken)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, $"api/kiosks/me/operator-logins/{id}/actions", cancellationToken)
            .ConfigureAwait(false);
        request.Content = JsonContent.Create(new { action, printer, limit }, options: JsonOptions);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task EndAsync(Guid id, CancellationToken cancellationToken)
    {
        using var request = await CreateAuthenticatedRequestAsync(HttpMethod.Post, $"api/kiosks/me/operator-logins/{id}/end", cancellationToken)
            .ConfigureAwait(false);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
