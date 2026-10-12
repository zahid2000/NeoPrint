using System.Net;

using MediatR;

using Microsoft.Extensions.Logging;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Admin;
using NeoPrint.Application.Localization;
using NeoPrint.Application.Refunds;
using NeoPrint.Domain.Admin;
using NeoPrint.Domain.Common;
using NeoPrint.Domain.Incidents;
using NeoPrint.Domain.Settings;

namespace NeoPrint.Application.Incidents;

/// <summary>
/// The Telegram bot that tells an operator about an incident (plan section
/// 2.1, "Admin təcili bildirişi"). Off while no bot token is configured.
/// </summary>
public interface ITelegramSender
{
    /// <summary>Whether a bot token is there. Without one nothing is sent and nothing fails.</summary>
    bool Enabled { get; }

    /// <param name="chatId">The operator's chat (<c>admin_users.TelegramChatId</c>).</param>
    /// <param name="text">Plain text: no markup is interpreted.</param>
    Task SendAsync(string chatId, string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// <c>IncidentNotificationConsumer</c> (plan section 6): the people who look
/// after the kiosk are told by e-mail and Telegram - the panel already knows.
/// </summary>
/// <remarks>
/// Urgent incidents only: from <c>Notification.MinSeverity</c> up - High
/// unless an administrator says otherwise, so Medium (a disk filling up, a
/// slow backend) stays in the panel: an inbox that rings for everything is
/// one nobody reads. E-mail is the channel; Telegram is phase 2 and goes only
/// when <c>Notification.TelegramEnabled</c> is on and the bot has its token
/// (<c>docs/DECISIONS.md</c>).
/// </remarks>
/// <returns>How many messages went out now.</returns>
public sealed record NotifyIncidentCommand(Guid IncidentId) : IRequest<int>;

public sealed class IncidentNotificationHandler : IRequestHandler<NotifyIncidentCommand, int>
{
    public const string EmailChannel = "email";

    public const string TelegramChannel = "telegram";

    private readonly IIncidentRepository _incidents;
    private readonly IKioskRepository _kiosks;
    private readonly IAdminUserRepository _admins;
    private readonly IPermissionResolver _permissions;
    private readonly IEmailSender _email;
    private readonly ITelegramSender _telegram;
    private readonly ISettingsReader _settings;
    private readonly IClock _clock;
    private readonly ILogger<IncidentNotificationHandler> _logger;

    /// <summary>The time in an e-mail: the same for every reader, so it says which clock.</summary>
    private const string TimeFormat = "yyyy-MM-dd HH:mm 'UTC'";

    public IncidentNotificationHandler(
        IIncidentRepository incidents,
        IKioskRepository kiosks,
        IAdminUserRepository admins,
        IPermissionResolver permissions,
        IEmailSender email,
        ITelegramSender telegram,
        ISettingsReader settings,
        IClock clock,
        ILogger<IncidentNotificationHandler> logger)
    {
        _clock = clock;
        _settings = settings;
        _incidents = incidents;
        _kiosks = kiosks;
        _admins = admins;
        _permissions = permissions;
        _email = email;
        _telegram = telegram;
        _logger = logger;
    }

    public async Task<int> Handle(NotifyIncidentCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var incident = await _incidents.FindByIdAsync(request.IncidentId, cancellationToken).ConfigureAwait(false);

        // Over before anybody could be told (a printer that blinked): no mail about nothing.
        if (incident is null || !incident.Open)
        {
            return 0;
        }

        // The more severe, the smaller the number: Critical is 0.
        if (incident.Severity > await LeastSeverityAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var telegram = _telegram.Enabled
                       && await _settings
                           .GetBoolAsync(SettingKeys.NotificationTelegramEnabled, false, cancellationToken)
                           .ConfigureAwait(false);

        var kiosk = incident.KioskId is { } kioskId
            ? await _kiosks.FindByIdAsync(kioskId, cancellationToken).ConfigureAwait(false)
            : null;
        var details = Messages.WriteStored(incident.Details) ?? string.Empty;
        var where = kiosk?.Code ?? Messages.Write(new UserMessage("email.incident.system"));
        var time = incident.CreatedAt.ToString(TimeFormat, System.Globalization.CultureInfo.InvariantCulture);
        var told = await _incidents.ListNotifiedAsync(incident.Id, cancellationToken).ConfigureAwait(false);
        var sent = 0;
        var failed = 0;

        foreach (var admin in await RecipientsAsync(incident, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(admin.Email) && !told.Contains(IncidentNotification.IdOf(incident.Id, EmailChannel, admin.Id)))
            {
                if (await TryAsync(
                            EmailChannel,
                            admin.Id,
                            incident,
                            () => _email.SendAsync(
                                admin.Email,
                                Messages.Write(UserMessage.Of("email.incident.subject", ("severity", incident.Severity.ToString()), ("where", where))),
                                // The details name a printer as the kiosk reported it: never markup.
                                Messages.Write(UserMessage.Of(
                                    "email.incident.body",
                                    ("severity", incident.Severity.ToString()),
                                    ("where", WebUtility.HtmlEncode(where)),
                                    ("details", WebUtility.HtmlEncode(details)),
                                    ("time", time))),
                                cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    sent++;
                }
                else
                {
                    failed++;
                }
            }

            if (telegram
                && !string.IsNullOrWhiteSpace(admin.TelegramChatId)
                && !told.Contains(IncidentNotification.IdOf(incident.Id, TelegramChannel, admin.Id)))
            {
                if (await TryAsync(
                            TelegramChannel,
                            admin.Id,
                            incident,
                            () => _telegram.SendAsync(
                                admin.TelegramChatId,
                                Messages.Write(UserMessage.Of(
                                    "telegram.incident", ("severity", incident.Severity.ToString()), ("where", where), ("details", details))),
                                cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    sent++;
                }
                else
                {
                    failed++;
                }
            }
        }

        if (failed > 0)
        {
            // The event is delivered again; whoever was reached is not told twice.
            throw new InvalidOperationException($"Incident {incident.Id}: {failed} notification(s) were not delivered.");
        }

        return sent;
    }

    /// <summary>
    /// <c>Notification.MinSeverity</c>; a value nobody could have saved
    /// (the validator refuses it) is read as the default, High.
    /// </summary>
    private async Task<IncidentSeverity> LeastSeverityAsync(CancellationToken cancellationToken)
    {
        var value = await _settings.GetAsync(SettingKeys.NotificationMinSeverity, cancellationToken).ConfigureAwait(false);

        return Enum.TryParse<IncidentSeverity>(value, ignoreCase: false, out var least) && Enum.IsDefined(least)
            ? least
            : IncidentSeverity.High;
    }

    /// <summary>
    /// Active accounts that may see incidents and this incident's kiosk; one
    /// about no kiosk (a refund, a fiscal receipt) goes to the accounts that
    /// see everything.
    /// </summary>
    private async Task<List<AdminUser>> RecipientsAsync(Incident incident, CancellationToken cancellationToken)
    {
        var recipients = new List<AdminUser>();

        foreach (var admin in (await _admins.ListAsync(cancellationToken).ConfigureAwait(false)).Where(admin => admin.IsActive))
        {
            var effective = await _permissions.ResolveAsync(admin.Id, cancellationToken).ConfigureAwait(false);

            if (effective.IsActive
                && effective.Has(Permissions.IncidentsView)
                && (incident.KioskId is { } kioskId ? effective.CanSee(kioskId) : effective.AllKiosks))
            {
                recipients.Add(admin);
            }
        }

        return recipients;
    }

    /// <summary>Sends, then writes down that it did - in a row of its own, whatever else is happening to the incident.</summary>
    private async Task<bool> TryAsync(
        string channel, Guid adminId, Incident incident, Func<Task> send, CancellationToken cancellationToken)
    {
        try
        {
            await send().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Who, by id only: no address and no chat id in the log.
            _logger.LogWarning(exception, "Incident {IncidentId}: the {Channel} message to admin {AdminId} failed.", incident.Id, channel, adminId);
            return false;
        }

        await _incidents
            .NoteNotifiedAsync(IncidentNotification.Of(incident.Id, channel, adminId, _clock.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
