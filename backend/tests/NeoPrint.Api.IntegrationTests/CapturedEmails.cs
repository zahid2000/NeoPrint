using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using NeoPrint.Application.Abstractions;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// An <see cref="IEmailSender"/> that keeps what it was asked to send.
/// </summary>
/// <remarks>
/// The test environment has no SMTP server - development uses Mailpit (plan
/// section 4) - and a test that confirms an address needs the code, which is
/// only ever stored as a hash. So the code is read where its owner reads it:
/// in the message.
/// </remarks>
public sealed class CapturedEmails : IEmailSender
{
    private readonly ConcurrentQueue<SentEmail> _sent = new();

    public IReadOnlyCollection<SentEmail> Sent => [.. _sent];

    public Task SendAsync(
        string to, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(new SentEmail(to, subject, htmlBody));

        return Task.CompletedTask;
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _sent.Enqueue(new SentEmail(message.To, message.Subject, message.HtmlBody, message.Attachments));

        return Task.CompletedTask;
    }

    /// <summary>The most recent message to an address, or null.</summary>
    public SentEmail? Latest(string to)
        => _sent
            .Where(email => string.Equals(email.To, to, StringComparison.OrdinalIgnoreCase))
            .LastOrDefault();

    /// <summary>
    /// The six digit code in the most recent message to an address.
    /// </summary>
    /// <exception cref="InvalidOperationException">No message, or no code in it.</exception>
    public string CodeFor(string to)
    {
        var email = Latest(to)
            ?? throw new InvalidOperationException($"No email was sent to {to}.");

        var match = Regex.Match(email.HtmlBody, @"\b(\d{6})\b");

        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException($"No six digit code in the email to {to}.");
    }
}

/// <param name="To">Recipient.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="HtmlBody">Body, as the API rendered it.</param>
/// <param name="Attachments">Files it carried: a receipt's PDF.</param>
public sealed record SentEmail(string To, string Subject, string HtmlBody, IReadOnlyList<EmailAttachment>? Attachments = null);
