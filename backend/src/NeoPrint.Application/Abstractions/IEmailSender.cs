namespace NeoPrint.Application.Abstractions;

/// <summary>
/// Sends transactional email: confirmation codes, receipts and refund notices.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken cancellationToken = default);
}
