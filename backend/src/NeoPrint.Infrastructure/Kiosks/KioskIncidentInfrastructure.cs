using System.Net.Http.Json;

using MassTransit;

using MediatR;

using Microsoft.Extensions.Configuration;

using NeoPrint.Application.Incidents;
using NeoPrint.Application.Notifications;
using NeoPrint.Contracts;

namespace NeoPrint.Infrastructure.Kiosks;

/// <summary>
/// <c>KioskHeartbeatMonitor</c> (plan section 6, every 15 seconds): kiosks
/// whose heartbeat stopped become Offline with a Critical incident (plan
/// section 9.6, scenario B). Nothing is cancelled or refunded here - what
/// happens to their orders is <c>StuckJobWatchdogJob</c>'s, later.
/// </summary>
public sealed class KioskHeartbeatMonitor
{
    public const string JobId = "kiosk-heartbeat-monitor";

    /// <summary>Six fields: Hangfire reads the first as seconds.</summary>
    public const string Every15Seconds = "*/15 * * * * *";

    private readonly ISender _sender;

    public KioskHeartbeatMonitor(ISender sender) => _sender = sender;

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 30)]
    public Task RunAsync(CancellationToken cancellationToken) => _sender.Send(new MarkSilentKiosksOfflineCommand(), cancellationToken);
}

/// <summary>
/// <c>IncidentNotificationConsumer</c> (plan section 6): e-mail and Telegram
/// for an incident the panel has already been told about.
/// </summary>
public sealed class IncidentNotificationConsumer : IConsumer<IncidentRaised>
{
    private readonly ISender _sender;

    public IncidentNotificationConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<IncidentRaised> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new NotifyIncidentCommand(context.Message.IncidentId), context.CancellationToken);
    }
}

/// <summary>
/// <c>AffectedUsersNotificationConsumer</c> (plan section 6): when a kiosk
/// stops, or one order stops at its printer, the registered owners are told
/// they may wait or take their money back (plan section 9.6, A.5).
/// </summary>
public sealed class AffectedUsersNotificationConsumer :
    IConsumer<KioskIncidentRaised>,
    IConsumer<PrintJobInterrupted>
{
    private readonly ISender _sender;

    public AffectedUsersNotificationConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<KioskIncidentRaised> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new NotifyAffectedUsersCommand(context.Message.KioskId), context.CancellationToken);
    }

    public Task Consume(ConsumeContext<PrintJobInterrupted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new NotifyJobInterruptedCommand(context.Message.JobId), context.CancellationToken);
    }
}

/// <summary>
/// A job the printer itself stopped means the kiosk is down now (plan section
/// 7.4): Paused, an incident, and section 9.6 from there - without waiting
/// for the heartbeats to agree.
/// </summary>
public sealed class KioskFaultOnInterruptedJobConsumer : IConsumer<PrintJobInterrupted>
{
    private readonly ISender _sender;

    public KioskFaultOnInterruptedJobConsumer(ISender sender) => _sender = sender;

    public Task Consume(ConsumeContext<PrintJobInterrupted> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _sender.Send(new DeclareKioskFaultForJobCommand(context.Message.JobId), context.CancellationToken);
    }
}

/// <summary>
/// The Telegram Bot API's <c>sendMessage</c>, with our own client (plan
/// section 2.0: no extra library). The bot token comes from OpenBao
/// (<c>NEOPRINT_TELEGRAM_BOT_TOKEN</c>); while there is none the channel is off.
/// </summary>
/// <remarks>
/// The token is part of the request's address, so the address must never be
/// logged: the named client is registered without the HTTP client loggers,
/// and a failure is reported by its status code alone.
/// </remarks>
public sealed class TelegramSender : ITelegramSender
{
    public const string HttpClientName = "telegram";

    public const string TokenKey = "NEOPRINT_TELEGRAM_BOT_TOKEN";

    /// <summary>Where the Bot API is; only tests point it elsewhere.</summary>
    public const string ApiUrlKey = "Telegram:ApiUrl";

    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _configuration;

    public TelegramSender(IHttpClientFactory clients, IConfiguration configuration)
    {
        _clients = clients;
        _configuration = configuration;
    }

    // Read each time: a token put into OpenBao later turns the channel on without a restart.
    public bool Enabled => !string.IsNullOrWhiteSpace(_configuration[TokenKey]);

    public async Task SendAsync(string chatId, string text, CancellationToken cancellationToken = default)
    {
        var token = _configuration[TokenKey];

        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var api = (_configuration[ApiUrlKey] ?? "https://api.telegram.org").TrimEnd('/');

        using var client = _clients.CreateClient(HttpClientName);
        HttpResponseMessage response;

        try
        {
            response = await client
                .PostAsJsonAsync(
                    new Uri($"{api}/bot{token}/sendMessage"),
                    new { chat_id = chatId, text, disable_web_page_preview = true },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // Rethrown without the inner exception: its request carries the address, and the address carries the token.
            throw new HttpRequestException($"Telegram could not be reached ({exception.HttpRequestError}).");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Telegram answered {(int)response.StatusCode}.");
            }
        }
    }
}
