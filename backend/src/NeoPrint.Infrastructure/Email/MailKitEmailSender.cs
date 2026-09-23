using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MimeKit;
using MimeKit.Text;

using NeoPrint.Application.Abstractions;

namespace NeoPrint.Infrastructure.Email;

/// <summary>
/// Sends transactional email over SMTP (plan section 2.1).
/// </summary>
/// <remarks>
/// MailKit rather than the framework's <c>SmtpClient</c>, which Microsoft marks
/// obsolete. In development the messages land in Mailpit.
/// </remarks>
public sealed class MailKitEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MailKitEmailSender> _logger;

    public MailKitEmailSender(IConfiguration configuration, ILogger<MailKitEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(
        string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        var host = _configuration["NEOPRINT_SMTP_HOST"];
        if (string.IsNullOrWhiteSpace(host))
        {
            // Not fatal: the rest of the flow works and the operator sees this
            // in the log. Registration would otherwise be impossible to test
            // without a mail server.
            _logger.LogWarning("NEOPRINT_SMTP_HOST is not configured; the message to {Recipient} was not sent.", to);
            return;
        }

        var port = int.TryParse(_configuration["NEOPRINT_SMTP_PORT"], out var configured) ? configured : 587;
        var user = _configuration["NEOPRINT_SMTP_USER"];
        var password = _configuration["NEOPRINT_SMTP_PASSWORD"];
        var from = _configuration["NEOPRINT_SMTP_FROM"] ?? "noreply@neoprint.az";
        var fromName = _configuration["NEOPRINT_SMTP_FROM_NAME"] ?? "NeoPrint";
        var useTls = bool.TryParse(_configuration["NEOPRINT_SMTP_USE_TLS"], out var tls) && tls;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new TextPart(TextFormat.Html) { Text = htmlBody };

        using var client = new SmtpClient();

        try
        {
            await client.ConnectAsync(
                    host,
                    port,
                    useTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(user))
            {
                // Mailpit accepts any credentials and needs no password, so an
                // unset password is normal in development.
                await client.AuthenticateAsync(user, password ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);

            // The recipient is logged; the body is not. It carries confirmation
            // codes and receipt details (plan section 2.13).
            _logger.LogInformation("Sent '{Subject}' to {Recipient}.", subject, to);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Sending '{Subject}' to {Recipient} failed.", subject, to);
            throw;
        }
    }
}
